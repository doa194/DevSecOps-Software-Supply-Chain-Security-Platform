"""Operational checks of the artifact registry (`sscp verify registry`, needs `--with ci`).

They protect what "build once, promote by digest" relies on: each robot account can do
exactly its job, candidate images are private, the tool mirror is read-only for everyone
but the bootstrap, and the offline vulnerability data is fresh enough for the trust policy.
"""
import base64
import json
from datetime import datetime, timedelta, timezone

import pytest

from sscp import credentials, http, registry, toolmirror, vulndb

pytestmark = pytest.mark.operational

POLICY_MAX_DATABASE_AGE = timedelta(days=7)


def _granted_actions(repository: str, actions: str, auth: tuple[str, str] | None) -> set[str]:
    """Asks Harbor's token service for a scope and returns the actions it actually granted."""
    with http.client("harbor") as client:
        response = client.get("/service/token", params={"service": "harbor-registry", "scope": f"repository:{repository}:{actions}"}, auth=auth)
    if response.status_code != 200:
        return set()
    payload = response.json()["token"].split(".")[1]
    claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
    return {action for entry in claims.get("access", []) for action in entry.get("actions", [])}


def _robot(name: str) -> tuple[str, str]:
    robot = next(r for r in registry.ROBOTS if r.name == name)
    return robot.username, credentials.get(f"harbor.robot.{name}")


def test_build_zone_robot_pushes_candidates_but_never_trusted_images():
    auth = _robot("candidate-pusher")

    assert _granted_actions(f"{registry.CANDIDATES}/probe", "push,pull", auth) == {"push", "pull"}
    assert _granted_actions(f"{registry.TRUSTED}/probe", "push,pull", auth) == set()


def test_security_zone_robot_can_only_pull_candidates():
    auth = _robot("candidate-reader")

    assert _granted_actions(f"{registry.CANDIDATES}/probe", "push,pull", auth) == {"pull"}
    assert _granted_actions(f"{registry.TRUSTED}/probe", "pull", auth) == set()


def test_candidate_images_are_private_and_tools_are_read_only():
    assert _granted_actions(f"{registry.CANDIDATES}/commerce-api", "pull", None) == set()
    assert _granted_actions(f"{toolmirror.PROJECT}/trivy", "push,pull", None) == {"pull"}


def test_offline_vulnerability_databases_are_fresh_enough_for_the_policy():
    trivy = json.loads(toolmirror.registry_tool("crane", ["manifest", vulndb.TRIVY_TARGET], timeout=60))
    grype = json.loads(toolmirror.registry_tool("crane", ["manifest", vulndb.GRYPE_TARGET], timeout=60))
    now = datetime.now(timezone.utc)

    for manifest in (trivy, grype):
        created = datetime.fromisoformat(manifest["annotations"]["org.opencontainers.image.created"].replace("Z", "+00:00"))
        assert now - created < POLICY_MAX_DATABASE_AGE, "run `sscp tools refresh-db`"
