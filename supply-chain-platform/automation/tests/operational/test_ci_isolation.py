"""Operational checks of the CI trust zones (`sscp verify ci-isolation`).

These observe the running runners, Vault and Gitea. They protect the properties the
trust model depends on: application workflows cannot reach platform runners, validation
jobs get neither Docker nor credentials, zone identities work only from their own
runner and only for their own secrets, each zone has a private Docker daemon, and the
Control Plane accepts only signed webhooks.
"""
import base64
import json
import time
import uuid

import pytest

from sscp import cizones, compose, credentials, docker, http, probe, shell, toolmirror
from sscp.services import gitea

pytestmark = pytest.mark.operational

PROBE_WORKFLOW = """
name: zone-probe
on: [pull_request]
jobs:
  # The validation runner accepts this job: it must see no Docker and no credentials.
  validation-probe:
    runs-on: sscp-validation
    steps:
      - run: |
          test ! -S /var/run/docker.sock || { echo "docker socket present"; exit 1; }
          test -z "$DOCKER_HOST" || { echo "DOCKER_HOST set"; exit 1; }
          if env | grep -q -E '^(SSCP_VAULT|VAULT_)'; then echo "credentials present"; exit 1; fi
          echo "no docker, no credentials"
  # No runner may accept this job: platform runners are registered to another repository.
  platform-runner-probe:
    runs-on: sscp-security
    steps:
      - run: env
"""


@pytest.fixture(scope="module")
def probe_pull_request():
    """Opens a pull request as a developer that adds a workflow asking for a platform runner."""
    api = gitea.Gitea()
    branch = f"probe/zone-isolation-{uuid.uuid4().hex[:8]}"
    api.post("repos/commerce/commerce-app/branches", {"new_branch_name": branch, "old_branch_name": "main"}, sudo="alice")
    api.post("repos/commerce/commerce-app/contents/.gitea/workflows/zone-probe.yaml", {
        "branch": branch, "message": "Probe the CI zone boundaries",
        "content": base64.b64encode(PROBE_WORKFLOW.encode()).decode(),
    }, sudo="alice")
    pull = api.post("repos/commerce/commerce-app/pulls", {"head": branch, "base": "main", "title": "Zone isolation probe"}, sudo="alice")
    runs = _wait_for_probe_runs(api, pull["number"])
    yield api, runs[0]
    # Clean up: close the pull request and delete the branch. The platform job that no
    # runner accepts is cancelled by Gitea's abandoned-job timeout. The pull request also
    # started the application's own validation workflow (a full .NET build); wait for it,
    # so that its load does not spill into the checks that run next.
    api.patch(f"repos/commerce/commerce-app/pulls/{pull['number']}", {"state": "closed"})
    api.request("DELETE", f"repos/commerce/commerce-app/branches/{branch}", expected=(403, 404))
    _wait_for_validation_workflow(api, pull["number"])


def _runs(api: gitea.Gitea, workflow: str, number: int) -> list[dict]:
    runs = (api.get("repos/commerce/commerce-app/actions/runs", params={"limit": 30}) or {}).get("workflow_runs", [])
    return [r for r in runs if r["path"] == f"{workflow}@refs/pull/{number}/head"]


# Polling below goes through http.wait_for, which treats a slow or failed request as "not
# yet": while a .NET build keeps the workstation's disk busy, Gitea can take longer than
# the client timeout to answer.

def _wait_for_validation_workflow(api: gitea.Gitea, number: int, timeout: float = 900) -> None:
    http.wait_for("the probe pull request's validation workflow",
                  lambda: all(r["status"] == "completed" for r in _runs(api, "validation.yaml", number)), timeout=timeout, interval=10)


def _wait_for_probe_runs(api: gitea.Gitea, number: int, timeout: float = 120) -> list[dict]:
    http.wait_for("the zone probe workflow", lambda: bool(_runs(api, "zone-probe.yaml", number)), timeout=timeout)
    return _runs(api, "zone-probe.yaml", number)


def _jobs(api: gitea.Gitea, run_id: int) -> dict[str, dict]:
    return {j["name"]: j for j in api.get(f"repos/commerce/commerce-app/actions/runs/{run_id}/jobs")["jobs"]}


def _completed_validation_job(api: gitea.Gitea, run_id: int, timeout: float = 600) -> dict:
    """The validation zone has one runner; the probe job may queue behind other jobs, and
    the first job after a runner restart pulls the .NET SDK image into its daemon."""
    http.wait_for("the validation probe job", lambda: _jobs(api, run_id)["validation-probe"]["status"] == "completed",
                  timeout=timeout, interval=5)
    return _jobs(api, run_id)["validation-probe"]


def test_runners_are_registered_at_their_documented_scope():
    api = gitea.Gitea()
    organisation = {r["name"] for r in (api.get("orgs/commerce/actions/runners") or {}).get("runners", [])}
    platform = {r["name"] for r in (api.get(f"repos/{cizones.PLATFORM_REPOSITORY}/actions/runners") or {}).get("runners", [])}

    assert organisation == {"sscp-validation"}
    assert platform == {"sscp-security", "sscp-build", "sscp-trust"}


def _approle_login_script(role_id: str, secret_id: str) -> str:
    return f"""
import json, ssl, urllib.request, urllib.error
context = ssl.create_default_context(cafile="/ca.crt")
request = urllib.request.Request("https://vault.sscp.test:8200/v1/auth/approle/login",
    data=json.dumps({{"role_id": "{role_id}", "secret_id": "{secret_id}"}}).encode(), method="POST")
try:
    with urllib.request.urlopen(request, context=context, timeout=10) as response:
        print(json.dumps({{"status": response.status}}))
except urllib.error.HTTPError as error:
    print(json.dumps({{"status": error.code}}))
"""


def test_zone_identity_is_useless_away_from_its_runner():
    zone = cizones.zone("security")
    script = _approle_login_script(cizones.role_id(zone), credentials.get(f"vault.approle.{zone.vault_role}.secret-id"))

    # Any other container on the edge network (a different source address).
    assert probe.run_python("sscp-edge", script)["status"] in (400, 403)


def test_zone_identity_reads_only_its_own_secrets():
    zone = cizones.zone("security")
    script = f"""
import json, urllib.request, urllib.error
def call(path, token=None, body=None):
    request = urllib.request.Request("https://vault.sscp.test:8200/v1/" + path, data=body, method="POST" if body else "GET")
    if token: request.add_header("X-Vault-Token", token)
    try:
        with urllib.request.urlopen(request, timeout=10) as response: return response.status, json.load(response)
    except urllib.error.HTTPError as error: return error.code, None
_, login = call("auth/approle/login", body=json.dumps({{"role_id": "{cizones.role_id(zone)}", "secret_id": "{credentials.get(f'vault.approle.{zone.vault_role}.secret-id')}"}}).encode())
token = login["auth"]["client_token"]
own, _ = call("kv/data/ci/security/controlplane", token)
other, _ = call("kv/data/ci/build/controlplane", token)
signing, _ = call("transit/sign/cosign-commerce", token, body=b'{{"input":"aGk="}}')
print(json.dumps({{"own": own, "other": other, "signing": signing}}))
"""
    assert probe.run_in_zone("security", script) == {"own": 200, "other": 403, "signing": 403}


def test_control_plane_refuses_unsigned_webhooks():
    body = json.dumps({"ref": "refs/heads/main", "after": "0" * 40, "repository": {"full_name": "commerce/commerce-app"}}).encode()
    with http.client("controlplane") as client:
        response = client.post("/api/webhooks/gitea", content=body,
                               headers={"X-Gitea-Event": "push", "X-Gitea-Signature": "0" * 64, "Content-Type": "application/json"})

    assert response.status_code == 401


def test_application_workflow_cannot_reach_a_platform_runner(probe_pull_request):
    api, run = probe_pull_request
    _completed_validation_job(api, run["id"])
    time.sleep(20)  # give any runner that could take the platform job ample time to do so

    platform_job = _jobs(api, run["id"])["platform-runner-probe"]
    assert platform_job["status"] in ("waiting", "queued"), platform_job
    assert not platform_job.get("runner_name"), platform_job


def test_validation_jobs_get_no_docker_socket_and_no_credentials(probe_pull_request):
    api, run = probe_pull_request

    job = _completed_validation_job(api, run["id"])

    assert job["conclusion"] == "success", job


def test_each_zone_has_a_private_docker_daemon_without_the_host_socket():
    images = {}
    for zone in ("validation", "security"):
        runner = docker.compose_container(compose.PROJECT, cizones.zone(zone).service)
        assert docker.exec_in(runner, ["test", "-S", "/var/run/docker.sock"], check=False).returncode != 0
        images[zone] = set(docker.exec_in(runner, ["docker", "images", "--format", "{{.Repository}}"]).stdout.split())

    # The SDK image the validation zone pulled is not visible to the security zone's daemon.
    sdk = toolmirror.references()["dotnetSdk"].split("@")[0]
    assert sdk in images["validation"]
    assert sdk not in images["security"]
