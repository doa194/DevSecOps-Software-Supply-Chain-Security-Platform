"""Harbor projects for commerce artifacts and one robot account per purpose (`sscp up --with ci`).

- commerce-candidates: images exactly as the build zone produced them, before any decision.
- commerce-trusted: signed images promoted by the trust zone after a positive decision,
  readable by the cluster only.

Each robot can do one thing on one project, and its secret goes only to the Vault path of
the zone that needs it. No robot can push to commerce-trusted except the promoter, whose
credentials are readable only with a signing grant.
"""
from __future__ import annotations

from dataclasses import dataclass

from sscp import credentials, http
from sscp.services import harbor, vault

CANDIDATES = "commerce-candidates"
TRUSTED = "commerce-trusted"


@dataclass(frozen=True)
class Robot:
    name: str
    description: str
    access: dict[str, list[str]]   # project -> actions on its repositories
    vault_path: str                # where the credentials are published

    @property
    def username(self) -> str:
        return f"robot${self.name}"


ROBOTS = [
    Robot("candidate-pusher", "Build zone: pushes candidate images", {CANDIDATES: ["push", "pull"]}, "kv/data/ci/build/harbor"),
    Robot("candidate-reader", "Security zone: pulls candidates to scan them", {CANDIDATES: ["pull"]}, "kv/data/ci/security/harbor"),
    # Trust zone, only through a signing grant: copies approved candidates into the trusted
    # project and attaches their signatures and attestations there.
    Robot("promoter", "Trust zone (signing grant only): promotes approved candidates and signs them",
          {CANDIDATES: ["pull"], TRUSTED: ["push", "pull"]}, "kv/data/ci/trust-signer/harbor"),
    # Cluster: the node pulls trusted images with it and Kyverno reads their signatures
    # and attestations. External Secrets delivers it from Vault to the two namespaces.
    Robot("cluster-puller", "Cluster: pulls trusted images and their signatures", {TRUSTED: ["pull"]}, "kv/data/platform/cluster/harbor-pull"),
]


def _secret(robot: Robot) -> str:
    # Harbor requires upper case, lower case and digits.
    return credentials.get(f"harbor.robot.{robot.name}", lambda: credentials.password(28) + "Aa1")


def _valid(robot: Robot) -> bool:
    project = next(iter(robot.access))
    with http.client("harbor") as client:
        response = client.get("/service/token", params={"service": "harbor-registry", "scope": f"repository:{project}/probe:pull"},
                              auth=(robot.username, _secret(robot)))
    return response.status_code == 200


def _ensure_immutable_release_tags(api: harbor.HarborApi) -> None:
    """Release tags (v*) in the trusted project can be written once and never moved."""
    project = api.request("GET", f"projects/{TRUSTED}").json()
    rules = api.request("GET", f"projects/{project['project_id']}/immutabletagrules").json()
    if any(rule.get("tag_selectors", [{}])[0].get("pattern") == "v*" for rule in rules):
        return
    api.request("POST", f"projects/{project['project_id']}/immutabletagrules", json={
        "disabled": False, "action": "immutable", "template": "immutable_template",
        "tag_selectors": [{"kind": "doublestar", "decoration": "matches", "pattern": "v*"}],
        "scope_selectors": {"repository": [{"kind": "doublestar", "decoration": "repoMatches", "pattern": "**"}]},
    })


def configure() -> None:
    api = harbor.HarborApi()
    api.ensure_project(CANDIDATES, public=False)
    api.ensure_project(TRUSTED, public=False)
    _ensure_immutable_release_tags(api)
    client = vault.bootstrap_client()
    for robot in ROBOTS:
        payload = {
            "name": robot.name, "description": robot.description, "duration": -1, "level": "system", "disable": False,
            "permissions": [
                {"kind": "project", "namespace": project, "access": [{"resource": "repository", "action": a} for a in actions]}
                for project, actions in robot.access.items()
            ],
        }
        existing = next((r for r in api.request("GET", "robots", params={"page_size": 100}).json() if r["name"] == robot.username), None)
        if existing is None:
            created = api.request("POST", "robots", json=payload).json()
            credentials.put(f"harbor.robot.{robot.name}", created["secret"])
        else:
            api.request("PUT", f"robots/{existing['id']}", json={**payload, "name": robot.username, "id": existing["id"]})
            if not _valid(robot):
                # Set a known secret again (for example after .local was recreated).
                api.request("PATCH", f"robots/{existing['id']}", json={"secret": _secret(robot)})
        client.write(robot.vault_path, {"data": {"username": robot.username, "password": _secret(robot), "registry": "harbor.sscp.test:8443"}})
