"""Operational checks of the software factory foundation (Docker Compose tier).

These tests observe the running platform. They protect the properties later stages
depend on: trusted TLS everywhere credentials flow, platform names resolvable from the
edge network, databases and evidence unreachable from it, per-service database
isolation, write-once evidence storage and Vault recovering from a restart.
"""
import json

import pytest

from sscp import compose, credentials, docker, http, probe, shell, versions
from sscp.services import minio, vault

pytestmark = pytest.mark.operational

TLS_SERVICES = ["gitea", "harbor", "vault", "keycloak"]
EDGE_TLS_TARGETS = [("gitea.sscp.test", 3000), ("harbor.sscp.test", 8443), ("vault.sscp.test", 8200), ("keycloak.sscp.test", 9443)]


@pytest.mark.parametrize("service", TLS_SERVICES)
def test_host_endpoints_present_certificates_from_the_local_ca(service):
    with http.client(service) as client:
        response = client.get("/")

    assert response.status_code < 500


def test_edge_services_resolve_and_verify_from_inside_the_edge_network():
    results = probe.tls_check("sscp-edge", EDGE_TLS_TARGETS)

    assert all(value.startswith("verified:") for value in results.values()), results


def test_evidence_store_serves_tls_inside_the_data_network():
    results = probe.tls_check("sscp-data", [("minio.sscp.test", 9000)])

    assert results["minio.sscp.test"].startswith("verified:"), results


def test_data_network_is_unreachable_from_the_edge_network():
    # A CI job or cluster workload sits on sscp-edge; it must not reach databases or raw evidence.
    targets = [("postgres.sscp.test", 5432), ("minio.sscp.test", 9000)]
    ips = {"postgres.sscp.test": "172.31.0.10", "minio.sscp.test": "172.31.0.11"}

    results = probe.tcp_check("sscp-edge", targets, ips)

    closed = {"resolved": False, "reachable": False}
    assert results == {"postgres.sscp.test": closed, "minio.sscp.test": closed}


def test_harbor_internals_are_reachable_only_through_its_proxy():
    harbor_db = shell.output(["docker", "inspect", "--format",
                              "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}",
                              docker.compose_container("sscp-harbor", "postgresql")]).split()[0]

    results = probe.tcp_check("sscp-edge", [("harbor-db", 5432)], {"harbor-db": harbor_db})

    assert results["harbor-db"]["reachable"] is False


def test_published_ports_are_bound_to_loopback_only():
    containers = shell.output(["docker", "ps", "-q", "--filter", "label=com.docker.compose.project"]).split()
    exposed = []
    for container in containers:
        ports = json.loads(shell.output(["docker", "inspect", "--format", "{{json .NetworkSettings.Ports}}", container])) or {}
        for bindings in ports.values():
            for binding in bindings or []:
                if binding["HostIp"] not in ("127.0.0.1", ""):
                    exposed.append((container, binding))
                if binding["HostIp"] == "":
                    exposed.append((container, binding))

    assert exposed == []


def test_vault_is_initialised_unsealed_and_audited():
    client = vault.bootstrap_client()

    health = client.health()
    audit_devices = client.read("sys/audit")["data"]

    assert health["initialized"] and not health["sealed"]
    assert "file/" in audit_devices


def test_vault_initial_root_token_has_been_revoked():
    root_token = json.loads(vault.INIT_FILE.read_text())["root_token"]

    response = vault.Vault(root_token)._client.get("/v1/auth/token/lookup-self", headers={"X-Vault-Token": root_token})

    assert response.status_code == 403


def test_bootstrap_token_cannot_sign():
    # Even the bootstrap administrator has no signing capability; only trust-zone grants do.
    client = vault.bootstrap_client()

    capabilities = client.write("sys/capabilities-self", {"paths": ["transit/sign/cosign-commerce"]})

    assert capabilities["capabilities"] == ["deny"]


def test_service_database_roles_cannot_open_other_services_databases():
    container = docker.compose_container(compose.PROJECT, "postgres")
    gitea_password = credentials.get("postgres.gitea")

    own = docker.exec_in(container, ["psql", "-h", "127.0.0.1", "-U", "gitea", "-d", "gitea", "-tAc", "select 1"],
                         env={"PGPASSWORD": gitea_password}, check=False)
    foreign = docker.exec_in(container, ["psql", "-h", "127.0.0.1", "-U", "gitea", "-d", "keycloak", "-tAc", "select 1"],
                             env={"PGPASSWORD": gitea_password}, check=False)

    assert own.returncode == 0
    assert foreign.returncode != 0 and "permission denied" in foreign.stderr


def test_evidence_cannot_be_deleted_with_the_control_plane_credentials():
    object_name = f"sscp/{minio.EVIDENCE_BUCKET}/operational-test/immutable.json"
    host = f"https://{minio.CONTROLPLANE_USER}:{minio.controlplane_secret()}@minio.sscp.test:9000"
    base = ["docker", "run", "--rm", "--user", "0:0", "--network", "sscp-data",
            "--mount", f"type=bind,source={minio.pki.CA_CERT.resolve().as_posix()},target=/tmp/mc/certs/CAs/ca.crt,readonly",
            "-e", f"MC_HOST_sscp={host}", versions.image("minioClient"), "--config-dir", "/tmp/mc"]
    shell.run([*base, "pipe", object_name], input_text='{"evidence": true}')

    delete = shell.run([*base, "rm", "--versions", "--force", object_name], check=False)
    listing = shell.run([*base, "ls", "--versions", object_name], check=False)

    assert delete.returncode != 0 or "immutable.json" in listing.stdout
    assert "immutable.json" in listing.stdout


def test_harbor_reports_every_component_healthy():
    with http.client("harbor") as client:
        health = client.get("/api/v2.0/health").json()

    unhealthy = [c["name"] for c in health["components"] if c["status"] != "healthy"]
    assert health["status"] == "healthy" and unhealthy == []


def test_gitea_requires_sign_in_to_view_anything():
    with http.client("gitea") as client:
        response = client.get("/api/v1/repos/search")

    assert response.status_code in (401, 403)


def test_keycloak_serves_the_master_realm_over_tls():
    with http.client("keycloak") as client:
        realm = client.get("/realms/master/.well-known/openid-configuration").json()

    assert realm["issuer"] == "https://keycloak.sscp.test:9443/realms/master"


def test_vault_recovers_from_a_restart_without_losing_state():
    # A restarted Vault comes back sealed; `sscp up` must unseal it with the stored keys and
    # all configuration (engines, audit device) must still be present.
    container = docker.compose_container(compose.PROJECT, "vault")
    shell.run(["docker", "restart", container])
    vault.wait_until_reachable()
    assert vault.Vault().health()["sealed"] is True

    vault.bootstrap()

    client = vault.bootstrap_client()
    assert client.health()["sealed"] is False
    assert "transit/" in client.read("sys/mounts")["data"]
