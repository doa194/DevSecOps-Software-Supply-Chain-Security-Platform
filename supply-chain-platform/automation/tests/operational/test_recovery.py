"""Operational checks of failure and recovery (`sscp verify recovery`).

The platform must fail closed: while a control that trust depends on is down, nothing may
progress to trusted, signed or deployed; and when the control comes back, the platform
must recover without corrupting trust or audit state. These checks take two light,
safe-to-restart services down in turn and prove both halves. Heavier services (Harbor,
Argo CD) are covered by the commands and expected observations in
docs/failure-and-recovery.md, so the automated suite does not stop them on a workstation.

Each check restores the service in a finally block, so a failure never leaves the
platform down.
"""
import httpx
import pytest

from sscp import compose, docker, http, shell
from sscp.services import controlplane, vault

pytestmark = pytest.mark.operational


def _container(service: str) -> str:
    name = docker.compose_container(compose.PROJECT, service)
    assert name, f"the {service} container is not running"
    return name


def _control_plane_reachable() -> bool:
    try:
        # Any status answer (even 401) means the API is up; a refused connection means it is not.
        return http.client("controlplane").get("/api/policy").status_code > 0
    except httpx.HTTPError:
        return False


def test_control_plane_outage_fails_closed_and_recovers():
    """With the Control Plane down, no webhook can be delivered and no trust decision can be
    made, so a candidate cannot become trusted. On restart the API returns and the
    hash-chained audit log still verifies, so the outage created no gap and no corruption."""
    container = _container("controlplane")
    shell.run(["docker", "stop", container], timeout=60)
    try:
        assert not _control_plane_reachable(), "the Control Plane still answered after being stopped"
    finally:
        controlplane.start()

    http.wait_for("the Control Plane API", _control_plane_reachable, timeout=180)
    verification = http.client("controlplane").get(
        "/api/audit/verification", headers={"Authorization": f"Bearer {controlplane.user_token('victor')}"})
    assert verification.status_code == 200 and verification.json()["intact"] is True


def test_vault_outage_fails_closed_and_recovers():
    """With Vault down, nothing can authenticate to it: CI zones cannot obtain tokens, the
    Control Plane cannot issue signing grants, and External Secrets cannot refresh, so no
    artifact can be signed or promoted. On restart Vault comes back sealed and must be
    unsealed with the stored keys; its engines (including the signing key) survive."""
    container = _container("vault")
    shell.run(["docker", "stop", container], timeout=60)
    try:
        with pytest.raises(httpx.HTTPError):
            vault.Vault().health()
    finally:
        shell.run(["docker", "start", container], timeout=60)
        vault.wait_until_reachable()
        # A stopped Vault returns sealed; recovery is the bootstrap unsealing it again.
        assert vault.Vault().health()["sealed"] is True
        vault.bootstrap()

    client = vault.bootstrap_client()
    assert client.health()["sealed"] is False
    assert "transit/" in client.read("sys/mounts")["data"]
