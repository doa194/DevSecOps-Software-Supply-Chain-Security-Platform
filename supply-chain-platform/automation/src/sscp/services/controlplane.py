"""Security Control Plane: database role, image, migrations and start-up.

The Control Plane runs as a restricted database role (controlplane_app) that can read,
insert and update rows but never delete them. Migrations run separately, in a one-off
container that receives the owner credentials only for its own lifetime; the long-running
container never sees them.
"""
from __future__ import annotations

from sscp import compose, credentials, docker, http, shell
from sscp.services import keycloak

RUNTIME_ROLE = "controlplane_app"
IMAGE = "sscp/controlplane:local"
PROFILES = ["controlplane"]


def runtime_password() -> str:
    return credentials.get("postgres.controlplane-app")


def ensure_runtime_role() -> None:
    """Creates (or re-keys) the runtime login role. Table grants are applied by `migrate`."""
    sql = f"""
DO $$ BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{RUNTIME_ROLE}') THEN
    CREATE ROLE {RUNTIME_ROLE} LOGIN;
  END IF;
END $$;
ALTER ROLE {RUNTIME_ROLE} WITH LOGIN PASSWORD '{runtime_password()}';
GRANT CONNECT ON DATABASE controlplane TO {RUNTIME_ROLE};
"""
    postgres = docker.compose_container(compose.PROJECT, "postgres")
    # SQL goes through stdin so the password never appears in a process list.
    docker.exec_in(postgres, ["psql", "-U", "postgres", "-d", "controlplane", "-v", "ON_ERROR_STOP=1", "-q"], input_text=sql)


def build() -> None:
    compose.compose("build", "controlplane", profiles=PROFILES, timeout=1800)


def migrate() -> None:
    """Runs `migrate` in a one-off container on the data network (it only needs PostgreSQL).

    A plain `docker run` rather than `compose run`: the Compose service has a fixed edge
    address that the running Control Plane already holds.
    """
    owner = f"Host=postgres.sscp.test;Database=controlplane;Username=controlplane;Password={credentials.get('postgres.controlplane')}"
    # `-e NAME` without a value copies it from this process's environment, keeping the
    # owner password off the docker command line.
    shell.run([
        "docker", "run", "--rm", "--network", "sscp-data", "--read-only", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges:true", "-e", "ConnectionStrings__migrations",
        IMAGE, "migrate",
    ], timeout=600, env={"ConnectionStrings__migrations": owner})


def start() -> None:
    compose.compose("up", "-d", "--wait", "--wait-timeout", "300", "controlplane", profiles=PROFILES, timeout=600)
    http.wait_for("Security Control Plane", lambda: http.client("controlplane").get("/api/policy").status_code == 401, timeout=120)


def user_token(username: str) -> str:
    return keycloak.user_token("platform", username, client_id="sscp-cli")


def zone_token(client_id: str) -> str:
    """Client-credentials token of a CI zone client (used by tests and by the runners' setup)."""
    with http.client("keycloak") as client:
        response = client.post("/realms/platform/protocol/openid-connect/token", data={
            "grant_type": "client_credentials", "client_id": client_id,
            "client_secret": keycloak.client_secret("platform", client_id),
        })
        response.raise_for_status()
        return response.json()["access_token"]
