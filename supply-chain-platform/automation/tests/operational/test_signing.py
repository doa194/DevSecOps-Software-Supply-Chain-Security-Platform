"""Operational checks of release signing (`sscp verify signing`, needs `--with ci` and at
least one release that reached promotion).

They protect what admission control relies on: the release key cannot leave Vault, no CI
identity can sign on its own, a signing grant works once and only on the trust runner,
and images in the trusted project carry signatures and trust-decision attestations that
verify offline with the published public key.
"""
import base64
import json

import pytest

from sscp import cizones, credentials, probe, registry, signing, toolmirror
from sscp.services import vault
from sscp.shell import CommandError

pytestmark = pytest.mark.operational

TRUST_DECISION_TYPE = "https://sscp.test/attestations/trust-decision/v1"
TRUSTED = f"{toolmirror.REGISTRY}/{registry.TRUSTED}"

# Prelude for probes: a tiny Vault client that reports status codes instead of raising.
# Job images trust the platform CA through the system store; plain probes mount it.
VAULT_CALL = """
import json, os, ssl, urllib.request, urllib.error
CONTEXT = ssl.create_default_context(cafile="/ca.crt") if os.path.exists("/ca.crt") else ssl.create_default_context()
def call(path, token=None, body=None):
    request = urllib.request.Request("https://vault.sscp.test:8200/v1/" + path,
        data=json.dumps(body).encode() if body is not None else None, method="POST" if body is not None else "GET")
    if token: request.add_header("X-Vault-Token", token)
    try:
        with urllib.request.urlopen(request, timeout=10, context=CONTEXT) as response: return response.status, json.load(response)
    except urllib.error.HTTPError as error: return error.code, None
DIGEST = "3q2+7w=="  # arbitrary input: probes never sign an image
"""


def _grant() -> tuple[str, str]:
    """Mints a grant the way the Control Plane does: a response-wrapped, single-use secret_id."""
    client = vault.bootstrap_client()
    response = client._client.post(f"/v1/auth/approle/role/{signing.SIGNER_ROLE}/secret-id",
                                   json={"metadata": json.dumps({"purpose": "operational-verification"})},
                                   headers={"X-Vault-Token": client.token, "X-Vault-Wrap-TTL": "120s"})
    response.raise_for_status()
    role_id = client.read(f"auth/approle/role/{signing.SIGNER_ROLE}/role-id")["data"]["role_id"]
    return role_id, response.json()["wrap_info"]["token"]


def _version(tag: str) -> list[int]:
    return [int(part) if part.isdigit() else 0 for part in tag.lstrip("v").split(".")]


def _cosign(args: list[str]) -> str:
    key = f"type=bind,source={signing.PUBLIC_KEY_FILE.resolve().as_posix()},target=/keys/cosign.pub,readonly"
    return toolmirror.registry_tool("cosign", [args[0], "--key", "/keys/cosign.pub", "--insecure-ignore-tlog=true", *args[1:]],
                                    mounts=[key], timeout=300)


@pytest.fixture(scope="module")
def released() -> tuple[str, str]:
    """(tag, image by digest) of the newest release of the API image in the trusted project."""
    repository = f"{TRUSTED}/commerce-api"
    tags = [t for t in toolmirror.registry_tool("crane", ["ls", repository], check=False, timeout=60).split() if t.startswith("v")]
    if not tags:
        pytest.skip("no release has been promoted yet; push a release tag with `sscp repo tag`")
    tag = max(tags, key=_version)
    return tag, f"{repository}@{toolmirror.registry_tool('crane', ['digest', f'{repository}:{tag}'], timeout=60)}"


def test_release_key_cannot_be_exported_from_vault():
    client = vault.bootstrap_client()
    key = client.read(f"transit/keys/{signing.KEY}")["data"]
    export = client._client.get(f"/v1/transit/export/signing-key/{signing.KEY}", headers={"X-Vault-Token": client.token})

    assert (key["type"], key["exportable"], key["allow_plaintext_backup"]) == ("ecdsa-p256", False, False)
    assert export.status_code in (400, 403)


def test_trust_zone_identity_alone_cannot_sign_or_read_promotion_credentials():
    zone = cizones.zone("trust")
    script = VAULT_CALL + f"""
_, login = call("auth/approle/login", body={{"role_id": "{cizones.role_id(zone)}", "secret_id": "{credentials.get(f'vault.approle.{zone.vault_role}.secret-id')}"}})
token = login["auth"]["client_token"]
sign, _ = call("transit/sign/{signing.KEY}/sha2-256", token, {{"input": DIGEST, "prehashed": True}})
promoter, _ = call("kv/data/ci/trust-signer/harbor", token)
print(json.dumps({{"sign": sign, "promoter": promoter}}))
"""
    assert probe.run_in_zone("trust", script) == {"sign": 403, "promoter": 403}


def test_signing_grant_works_once_and_only_on_the_trust_runner():
    role_id, wrapped = _grant()
    script = VAULT_CALL + f"""
unwrap, body = call("sys/wrapping/unwrap", "{wrapped}", {{}})
secret_id = body["data"]["secret_id"]
first, login = call("auth/approle/login", body={{"role_id": "{role_id}", "secret_id": secret_id}})
sign, _ = call("transit/sign/{signing.KEY}/sha2-256", login["auth"]["client_token"], {{"input": DIGEST, "prehashed": True}})
second, _ = call("auth/approle/login", body={{"role_id": "{role_id}", "secret_id": secret_id}})
unwrap_again, _ = call("sys/wrapping/unwrap", "{wrapped}", {{}})
print(json.dumps({{"unwrap": unwrap, "first": first, "sign": sign, "second": second, "unwrap_again": unwrap_again}}))
"""
    assert probe.run_in_zone("trust", script) == {"unwrap": 200, "first": 200, "sign": 200, "second": 400, "unwrap_again": 400}

    # A grant intercepted and opened anywhere else is useless there.
    role_id, wrapped = _grant()
    script = VAULT_CALL + f"""
_, body = call("sys/wrapping/unwrap", "{wrapped}", {{}})
login, _ = call("auth/approle/login", body={{"role_id": "{role_id}", "secret_id": body["data"]["secret_id"]}})
print(json.dumps({{"login": login}}))
"""
    assert probe.run_python("sscp-edge", script)["login"] in (400, 403)


def test_trusted_release_images_verify_offline_with_the_published_key(released):
    _, image = released

    _cosign(["verify", image])
    output = _cosign(["verify-attestation", "--type", TRUST_DECISION_TYPE, image])

    statements = [json.loads(base64.b64decode(json.loads(line)["payload"])) for line in output.splitlines() if line.startswith("{")]
    digest = image.split("@sha256:")[1]
    assert any(s["subject"][0]["digest"]["sha256"] == digest and s["predicate"]["decision"]["outcome"] in ("PASS", "PASS_WITH_EXCEPTION")
               for s in statements)


def test_unsigned_images_do_not_verify():
    unsigned = toolmirror.references()["dotnetSdk"]

    with pytest.raises(CommandError, match="no signatures found"):
        _cosign(["verify", unsigned])


def test_release_tags_in_the_trusted_project_are_immutable(released):
    tag, image = released
    repository, digest = image.split("@")

    # Try to point the API image's release tag at the gateway image of the same release.
    toolmirror.registry_tool("crane", ["copy", f"{TRUSTED}/commerce-gateway:{tag}", f"{repository}:{tag}"], check=False, timeout=120)

    assert toolmirror.registry_tool("crane", ["digest", f"{repository}:{tag}"], timeout=60) == digest
