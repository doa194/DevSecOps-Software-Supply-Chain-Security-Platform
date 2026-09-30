"""Vault: initialisation, unsealing and base configuration.

Root-of-trust handling:
- `vault operator init` produces three unseal key shares (two are needed) and a root
  token. They are written to .local/secrets/vault-init.json and never mounted anywhere.
- The root token is used once to create a bootstrap administrator token and is then
  revoked. Later bootstrap runs use the bootstrap token; steady-state components never
  see either and only get narrowly scoped AppRole or Kubernetes identities.
- If the bootstrap token is lost, a new root token can be generated from the unseal keys
  with `vault operator generate-root`.
"""
from __future__ import annotations

import json

import httpx

from sscp import console, credentials, http, paths

INIT_FILE = paths.SECRETS_DIR / "vault-init.json"
BOOTSTRAP_TOKEN_KEY = "vault.bootstrap-token"

BOOTSTRAP_POLICY = """
# Administrative policy used only by the workstation bootstrap automation.
path "sys/*"          { capabilities = ["create", "read", "update", "delete", "list", "sudo"] }
path "auth/*"         { capabilities = ["create", "read", "update", "delete", "list", "sudo"] }
path "identity/*"     { capabilities = ["create", "read", "update", "delete", "list"] }
path "kv/*"           { capabilities = ["create", "read", "update", "delete", "list"] }
path "transit/keys/*" { capabilities = ["create", "read", "update", "list"] }
path "transit/keys"   { capabilities = ["list"] }
# The bootstrap may configure signing keys but deliberately cannot sign.
path "transit/sign/*" { capabilities = ["deny"] }
"""


class Vault:
    def __init__(self, token: str | None = None):
        self._client = http.client("vault")
        self.token = token

    def request(self, method: str, path: str, **kwargs) -> httpx.Response:
        headers = kwargs.pop("headers", {})
        if self.token:
            headers["X-Vault-Token"] = self.token
        response = self._client.request(method, f"/v1/{path}", headers=headers, **kwargs)
        if response.status_code >= 400:
            raise RuntimeError(f"Vault {method} {path} failed: {response.status_code} {response.text[:300]}")
        return response

    def read(self, path: str) -> dict | None:
        headers = {"X-Vault-Token": self.token} if self.token else {}
        response = self._client.get(f"/v1/{path}", headers=headers)
        if response.status_code == 404:
            return None
        if response.status_code >= 400:
            raise RuntimeError(f"Vault GET {path} failed: {response.status_code} {response.text[:300]}")
        return response.json()

    def write(self, path: str, payload: dict | None = None) -> dict | None:
        response = self.request("POST", path, json=payload or {})
        return response.json() if response.content else None

    def health(self) -> dict:
        return self._client.get("/v1/sys/health", params={"sealedcode": 200, "uninitcode": 200, "standbyok": True}).json()

    def is_active(self) -> bool:
        return self._client.get("/v1/sys/health").status_code == 200


def wait_until_reachable() -> None:
    http.wait_for("Vault API", lambda: "initialized" in Vault().health(), timeout=120)


def initialise() -> None:
    vault = Vault()
    if vault.health()["initialized"]:
        return
    result = vault.write("sys/init", {"secret_shares": 3, "secret_threshold": 2})
    paths.SECRETS_DIR.mkdir(parents=True, exist_ok=True)
    INIT_FILE.write_text(json.dumps(result, indent=2), encoding="utf-8")
    console.info("Vault initialised; unseal keys and initial root token stored in .local/secrets/vault-init.json")


def unseal() -> None:
    vault = Vault()
    if vault.health()["sealed"]:
        if not INIT_FILE.exists():
            raise RuntimeError("Vault is sealed and .local/secrets/vault-init.json is missing; the data cannot be recovered")
        keys = json.loads(INIT_FILE.read_text(encoding="utf-8"))["keys_base64"]
        for key in keys:
            if not vault.write("sys/unseal", {"key": key})["sealed"]:
                break
        else:
            raise RuntimeError("Vault is still sealed after using every stored unseal key")
    # An unsealed Raft node still has to elect itself leader before it answers requests;
    # on slow disks that takes several seconds. /sys/health returns 200 only when active.
    http.wait_for("Vault active node", lambda: vault.is_active(), timeout=180)


def bootstrap_client() -> Vault:
    """Returns a client with the bootstrap administrator token, creating it on first use."""
    token = credentials.find(BOOTSTRAP_TOKEN_KEY)
    if token:
        return Vault(token)

    root_token = json.loads(INIT_FILE.read_text(encoding="utf-8"))["root_token"]
    root = Vault(root_token)
    root.request("PUT", "sys/policies/acl/sscp-bootstrap", json={"policy": BOOTSTRAP_POLICY})
    created = root.write("auth/token/create", {
        "policies": ["sscp-bootstrap"],
        "display_name": "sscp-bootstrap",
        "period": "720h",  # renewable; stays valid while the bootstrap is run at least monthly
        "no_parent": True,
        "no_default_policy": False,
    })
    token = created["auth"]["client_token"]
    credentials.put(BOOTSTRAP_TOKEN_KEY, token)
    # The root token is no longer needed and must not stay usable.
    root.write("auth/token/revoke-self")
    console.info("Vault root token revoked; bootstrap uses a scoped administrator token")
    return Vault(token)


def renew_bootstrap_token(vault: Vault) -> None:
    vault.write("auth/token/renew-self")


def configure_base(vault: Vault) -> None:
    """Secret engines, auth methods and the audit device every later step relies on."""
    mounts = vault.read("sys/mounts")["data"]
    if "kv/" not in mounts:
        vault.write("sys/mounts/kv", {"type": "kv", "options": {"version": "2"}, "description": "Platform and workload secrets"})
    if "transit/" not in mounts:
        vault.write("sys/mounts/transit", {"type": "transit", "description": "Signing keys that never leave Vault"})

    auths = vault.read("sys/auth")["data"]
    if "approle/" not in auths:
        vault.write("sys/auth/approle", {"type": "approle", "description": "CI zone and service identities"})

    audits = vault.read("sys/audit")["data"]
    if "file/" not in audits:
        # Every request and response (secrets hashed) is recorded; signing operations can be
        # traced back to the release they were granted for.
        vault.write("sys/audit/file", {"type": "file", "options": {"file_path": "/vault/logs/audit.log"}})


def bootstrap() -> Vault:
    wait_until_reachable()
    initialise()
    unseal()
    vault = bootstrap_client()
    renew_bootstrap_token(vault)
    configure_base(vault)
    return vault
