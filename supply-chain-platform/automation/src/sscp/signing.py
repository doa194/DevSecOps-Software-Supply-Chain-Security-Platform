"""Release signing in Vault: the Transit key, the trust-signer identity and the Control
Plane's grant-issuing identity (`sscp up`).

- `cosign-commerce` is an ECDSA P-256 Transit key that cannot be exported or backed up in
  plain text: the private key never exists outside Vault.
- `trust-signer` is the only identity that can use it. Nobody holds a standing
  credential for it: for each approved release the Control Plane asks Vault for one
  single-use, response-wrapped secret_id, which works only from the trust runner's
  address and yields a short-lived token.
- The Control Plane's own AppRole may only mint those secret_ids (it cannot sign).
"""
from __future__ import annotations

import json

from sscp import cizones, credentials, paths
from sscp.services import vault

KEY = "cosign-commerce"
SIGNER_ROLE = "trust-signer"
CONTROLPLANE_ROLE = "controlplane"
CONTROLPLANE_ADDRESS = "172.30.0.14"
PUBLIC_KEY_FILE = paths.GENERATED_DIR / "cosign-commerce.pub"

SIGNER_POLICY = f"""
# Sign with the release key and read its public part; read the promotion credentials.
# Cosign's Vault client signs at transit/sign/<key>/<hash algorithm>.
path "transit/sign/{KEY}"      {{ capabilities = ["update"] }}
path "transit/sign/{KEY}/*"    {{ capabilities = ["update"] }}
path "transit/keys/{KEY}"      {{ capabilities = ["read"] }}
path "kv/data/ci/trust-signer/*" {{ capabilities = ["read"] }}
"""

GRANTS_POLICY = f"""
# Mint single-use secret_ids for the trust-signer role (always response-wrapped by the
# caller) and read its role_id. Nothing else: the Control Plane cannot sign.
path "auth/approle/role/{SIGNER_ROLE}/secret-id" {{ capabilities = ["update"] }}
path "auth/approle/role/{SIGNER_ROLE}/role-id"   {{ capabilities = ["read"] }}
"""


def configure() -> None:
    client = vault.bootstrap_client()
    if client.read(f"transit/keys/{KEY}") is None:
        client.write(f"transit/keys/{KEY}", {"type": "ecdsa-p256", "exportable": False, "allow_plaintext_backup": False})
    trust_runner = cizones.zone("trust").address
    client.request("PUT", f"sys/policies/acl/{SIGNER_ROLE}", json={"policy": SIGNER_POLICY})
    client.write(f"auth/approle/role/{SIGNER_ROLE}", {
        "token_policies": [SIGNER_ROLE], "token_ttl": "10m", "token_max_ttl": "15m", "token_type": "service",
        # Usable once, for ten minutes, and only from the trust runner.
        "secret_id_num_uses": 1, "secret_id_ttl": "10m",
        "secret_id_bound_cidrs": [f"{trust_runner}/32"], "token_bound_cidrs": [f"{trust_runner}/32"],
        "bind_secret_id": True,
    })

    client.request("PUT", f"sys/policies/acl/{CONTROLPLANE_ROLE}-grants", json={"policy": GRANTS_POLICY})
    client.write(f"auth/approle/role/{CONTROLPLANE_ROLE}", {
        "token_policies": [f"{CONTROLPLANE_ROLE}-grants"], "token_ttl": "15m", "token_max_ttl": "1h", "token_type": "service",
        "secret_id_ttl": "0", "secret_id_bound_cidrs": [f"{CONTROLPLANE_ADDRESS}/32"], "token_bound_cidrs": [f"{CONTROLPLANE_ADDRESS}/32"],
        "bind_secret_id": True,
    })
    role_id = client.read(f"auth/approle/role/{CONTROLPLANE_ROLE}/role-id")["data"]["role_id"]
    credentials.put("vault.approle.controlplane.role-id", role_id)
    stored = credentials.find("vault.approle.controlplane.secret-id")
    if not stored or not cizones._secret_id_valid(client, CONTROLPLANE_ROLE, stored):
        created = client.write(f"auth/approle/role/{CONTROLPLANE_ROLE}/secret-id", {"metadata": json.dumps({"service": "controlplane"})})
        credentials.put("vault.approle.controlplane.secret-id", created["data"]["secret_id"])

    # The public key is not secret; admission control and verifiers need it.
    key = client.read(f"transit/keys/{KEY}")["data"]
    latest = str(key["latest_version"])
    PUBLIC_KEY_FILE.parent.mkdir(parents=True, exist_ok=True)
    PUBLIC_KEY_FILE.write_text(key["keys"][latest]["public_key"], encoding="utf-8", newline="\n")
