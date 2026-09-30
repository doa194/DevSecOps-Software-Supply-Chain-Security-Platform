"""Operational checks of the deployed Security Control Plane.

The API behaviour itself is covered by the .NET component tests; these checks cover what
only the deployment can show: real Keycloak tokens are accepted with the right meaning,
unauthenticated endpoints exist only on the internal management port, the service runs
with its restricted database role and a locked-down container, and it recovers from a
restart with its audit chain intact.
"""
import json
import uuid

import pytest

from sscp import compose, docker, http, probe, shell
from sscp.services import controlplane

pytestmark = pytest.mark.operational


def _get(path: str, token: str | None = None):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    with http.client("controlplane") as client:
        return client.get(path, headers=headers)


def test_api_requires_a_token():
    assert _get("/api/policy").status_code == 401


def test_viewer_token_from_keycloak_reads_the_active_policy():
    response = _get("/api/policy", controlplane.user_token("victor"))

    assert response.status_code == 200
    assert response.json()["version"].startswith("commerce-trust-policy@sha256:")


def test_zone_token_from_keycloak_is_a_pipeline_not_a_person():
    token = controlplane.zone_token("ci-security-zone")

    assert _get("/api/policy", token).status_code == 403
    with http.client("controlplane") as client:
        response = client.post(f"/api/builds/{uuid.uuid4()}/evaluation", json={"runId": 1},
                               headers={"Authorization": f"Bearer {token}"})
    # Authorised as the security zone, then refused by the application: no such build.
    assert response.status_code == 404


def test_people_cannot_call_pipeline_endpoints():
    with http.client("controlplane") as client:
        response = client.post(f"/api/builds/{uuid.uuid4()}/evaluation", json={"runId": 1},
                               headers={"Authorization": f"Bearer {controlplane.user_token('paula')}"})

    assert response.status_code == 403


def test_health_and_metrics_exist_only_on_the_internal_management_port():
    script = """
import json, urllib.request, urllib.error
def status(url):
    try:
        with urllib.request.urlopen(url, timeout=5) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, ""
ready = status("http://controlplane.sscp.test:9464/health/ready")
metrics = status("http://controlplane.sscp.test:9464/metrics")
api = status("http://controlplane.sscp.test:9464/api/policy")
print(json.dumps({"ready": ready[0], "metrics": metrics[0], "hasTrustMetrics": "sscp_" in metrics[1], "api": api[0]}))
"""
    result = probe.run_python("sscp-edge", script)

    assert result == {"ready": 200, "metrics": 200, "hasTrustMetrics": True, "api": 404}
    assert _get("/health/ready").status_code == 404
    assert shell.run(["docker", "port", _container(), "9464"], check=False).stdout.strip() == ""


def test_runs_as_the_restricted_database_role():
    postgres = docker.compose_container(compose.PROJECT, "postgres")
    users = docker.exec_in(postgres, ["psql", "-U", "postgres", "-At", "-c",
                                      "SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = 'controlplane' AND usename IS NOT NULL"]).stdout.split()

    assert users == [controlplane.RUNTIME_ROLE]


def test_container_is_read_only_non_root_and_without_capabilities():
    config = json.loads(shell.output(["docker", "inspect", "--format",
                                      "{{json .HostConfig}}", _container()]))
    user = shell.output(["docker", "inspect", "--format", "{{.Config.User}}", _container()])

    assert config["ReadonlyRootfs"] is True
    assert config["CapDrop"] == ["ALL"]
    assert "no-new-privileges:true" in config["SecurityOpt"]
    assert user not in ("", "0", "root")


def test_recovers_from_a_restart_with_the_audit_chain_intact():
    shell.run(["docker", "restart", _container()], timeout=120)
    docker.wait_healthy(_container(), timeout=180)

    response = _get("/api/audit/verification", controlplane.user_token("victor"))

    assert response.status_code == 200
    assert response.json()["intact"] is True


def _container() -> str:
    name = docker.compose_container(compose.PROJECT, "controlplane")
    assert name, "the controlplane container is not running"
    return name
