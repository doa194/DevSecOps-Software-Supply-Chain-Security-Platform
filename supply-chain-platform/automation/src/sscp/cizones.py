"""CI trust zones: runner identities in Vault, runner configuration and registration.

Four runners, one per zone (see docs/trust-boundaries.md):

- validation: registered to the `commerce` organisation, so it is the only runner the
  application repository can use. Jobs get no Docker socket and no credentials.
- security, build, trust: registered to the platform repository only. Each job receives
  its zone's Vault AppRole, which Vault accepts only from that runner's fixed address.

Every runner hosts its own rootless Docker daemon; nothing is shared between zones.
"""
from __future__ import annotations

import json
import time
from dataclasses import dataclass

import yaml

from sscp import compose, credentials, paths, sourcecontrol, toolmirror
from sscp.services import gitea, keycloak, vault

PLATFORM_REPOSITORY = "platform/supply-chain-platform"
RUNNERS_DIR = paths.GENERATED_DIR / "runners"
CA_IN_RUNNER = "/etc/sscp/certs/sscp-root-ca.crt"


@dataclass(frozen=True)
class Zone:
    name: str
    address: str
    label: str
    docker: bool
    vault_role: str | None
    keycloak_client: str | None
    capacity: int

    @property
    def scope(self) -> str:
        return "org" if self.name == "validation" else "repo"

    @property
    def service(self) -> str:
        return f"runner-{self.name}"


ZONES = [
    Zone("validation", "172.30.0.21", "sscp-validation", docker=False, vault_role=None, keycloak_client=None, capacity=2),
    Zone("security", "172.30.0.22", "sscp-security", docker=True, vault_role="ci-security", keycloak_client="ci-security-zone", capacity=1),
    Zone("build", "172.30.0.23", "sscp-build", docker=True, vault_role="ci-build", keycloak_client="ci-build-zone", capacity=1),
    Zone("trust", "172.30.0.24", "sscp-trust", docker=False, vault_role="ci-trust", keycloak_client="ci-trust-zone", capacity=1),
]


def zone(name: str) -> Zone:
    return next(z for z in ZONES if z.name == name)


# ---------------------------------------------------------------- Vault identities

def _zone_secrets(z: Zone) -> dict[str, dict[str, str]]:
    secrets = {"controlplane": {"client_id": z.keycloak_client, "client_secret": keycloak.client_secret("platform", z.keycloak_client)}}
    if z.name in ("security", "build"):
        # Read-only access to application source, to fetch exact commits.
        secrets["gitea"] = {"token": sourcecontrol.source_reader_token()}
    if z.name == "security":
        # Dynamic testing: the commerce test personas and the identity-admin client the
        # candidate API needs in the security-test environment.
        personas = yaml.safe_load((keycloak.REALMS_DIR / "commerce.yaml").read_text(encoding="utf-8"))["personas"]
        secrets["commerce-test"] = {
            "personas": json.dumps({name: keycloak.persona_password("commerce", name) for name in personas}),
            "identity_admin_secret": keycloak.client_secret("commerce", "commerce-identity-admin"),
        }
    return secrets


def _secret_id_valid(client: vault.Vault, role: str, secret_id: str) -> bool:
    response = client._client.post(f"/v1/auth/approle/role/{role}/secret-id/lookup",
                                   json={"secret_id": secret_id}, headers={"X-Vault-Token": client.token})
    return response.status_code == 200 and bool(response.content) and response.json().get("data") is not None


def configure_vault() -> None:
    """Creates each zone's AppRole (bound to its runner address), policy and secrets."""
    client = vault.bootstrap_client()
    for z in ZONES:
        if z.vault_role is None:
            continue
        client.request("PUT", f"sys/policies/acl/{z.vault_role}",
                       json={"policy": f'path "kv/data/ci/{z.name}/*" {{ capabilities = ["read"] }}\n'})
        client.write(f"auth/approle/role/{z.vault_role}", {
            "token_policies": [z.vault_role],
            "token_ttl": "15m", "token_max_ttl": "30m", "token_type": "service",
            # Both the secret_id and every token it produces work only from this runner.
            "secret_id_bound_cidrs": [f"{z.address}/32"],
            "token_bound_cidrs": [f"{z.address}/32"],
            "secret_id_ttl": "0", "bind_secret_id": True,
        })
        key = f"vault.approle.{z.vault_role}.secret-id"
        secret_id = credentials.find(key)
        if not secret_id or not _secret_id_valid(client, z.vault_role, secret_id):
            # Vault expects the metadata as a JSON-encoded string.
            created = client.write(f"auth/approle/role/{z.vault_role}/secret-id", {"metadata": json.dumps({"zone": z.name})})
            credentials.put(key, created["data"]["secret_id"])
        for name, data in _zone_secrets(z).items():
            client.write(f"kv/data/ci/{z.name}/{name}", {"data": data})


def role_id(z: Zone) -> str:
    return vault.bootstrap_client().read(f"auth/approle/role/{z.vault_role}/role-id")["data"]["role_id"]


# ---------------------------------------------------------------- runner configuration

def _config(z: Zone, job_image: str) -> dict:
    if z.vault_role:
        envs = {
            "SSCP_ZONE": z.name,
            "VAULT_ADDR": "https://vault.sscp.test:8200",
            "SSCP_VAULT_ROLE_ID": role_id(z),
            "SSCP_VAULT_SECRET_ID": credentials.get(f"vault.approle.{z.vault_role}.secret-id"),
            "SSCP_CONTROLPLANE_URL": "https://controlplane.sscp.test:8443",
            "SSCP_KEYCLOAK_TOKEN_URL": "https://keycloak.sscp.test:9443/realms/platform/protocol/openid-connect/token",
            "SSCP_GITEA_URL": "https://gitea.sscp.test:3000",
            "SSCP_REGISTRY": toolmirror.REGISTRY,
        }
    else:
        envs = {"DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1", "SSCP_CA_FILE": "/etc/sscp/ca.crt"}
    return {
        "log": {"level": "info"},
        "runner": {
            "file": "/data/.runner", "capacity": z.capacity, "timeout": "1h", "envs": envs,
            "labels": [f"{z.label}:docker://{job_image}"],
            "fetch_interval": "2s", "fetch_interval_max": "5s",
        },
        # No shared action cache: nothing written by one job can be read by another.
        "cache": {"enabled": False},
        "container": {
            "network": "",  # a fresh network per job inside the zone's private daemon
            "privileged": False,
            # The platform CA for jobs whose image does not already contain it.
            "options": f"--mount type=bind,source={CA_IN_RUNNER},target=/etc/sscp/ca.crt,readonly",
            # Only the platform CA may be mounted (the options line above needs it listed).
            "valid_volumes": [CA_IN_RUNNER],
            # "-": jobs do not get the daemon's socket. Empty: the zone's private daemon.
            "docker_host": "" if z.docker else "-",
            "force_pull": False,
            "require_docker": True,
        },
    }


def write_configs(ci_tools: str) -> list[Zone]:
    """Writes each runner's configuration; returns the zones whose configuration changed."""
    sdk = toolmirror.references()["dotnetSdk"]
    changed = []
    for z in ZONES:
        path = RUNNERS_DIR / z.name / "config.yaml"
        path.parent.mkdir(parents=True, exist_ok=True)
        job_image = sdk if z.name == "validation" else ci_tools
        content = yaml.safe_dump(_config(z, job_image), sort_keys=False)
        if not path.exists() or path.read_text(encoding="utf-8") != content:
            path.write_text(content, encoding="utf-8", newline="\n")
            changed.append(z)
    return changed


def registration_tokens() -> dict[str, str]:
    api = gitea.Gitea()
    organisation = api.post("orgs/commerce/actions/runners/registration-token")["token"]
    repository = api.post(f"repos/{PLATFORM_REPOSITORY}/actions/runners/registration-token")["token"]
    return {z.name: organisation if z.scope == "org" else repository for z in ZONES}


def start(changed: list[Zone]) -> None:
    tokens = registration_tokens()
    env = {f"RUNNER_TOKEN_{name.upper()}": token for name, token in tokens.items()}
    # Registration tokens go through the environment, not the command line or env file.
    compose.compose("up", "-d", *[z.service for z in ZONES], profiles=["ci"], timeout=900, env=env)
    # The configuration is a mounted file, so Compose does not notice when it changes.
    if changed:
        compose.compose("restart", *[z.service for z in changed], profiles=["ci"], timeout=300)
    wait_online()


def runners() -> list[dict]:
    api = gitea.Gitea()
    listed = (api.get("orgs/commerce/actions/runners") or {}).get("runners", [])
    listed += (api.get(f"repos/{PLATFORM_REPOSITORY}/actions/runners") or {}).get("runners", [])
    return listed


def wait_online(timeout: float = 300) -> None:
    deadline = time.monotonic() + timeout
    expected = {f"sscp-{z.name}" for z in ZONES}
    online: set[str] = set()
    while time.monotonic() < deadline:
        online = {r["name"] for r in runners() if r.get("status") in ("online", "idle", "active")}
        if expected <= online:
            return
        time.sleep(5)
    raise TimeoutError(f"runners not online after {timeout:.0f}s: {sorted(expected - online)}")
