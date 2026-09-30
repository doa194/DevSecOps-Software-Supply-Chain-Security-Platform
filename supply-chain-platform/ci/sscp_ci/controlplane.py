"""Calls from a CI zone job to the Security Control Plane, always with the zone's token and
the job's run id (the Control Plane accepts them only from the run it dispatched)."""
from __future__ import annotations

import json
import os
import sys
import time
from pathlib import Path

from sscp_ci import http, zone


# Two jobs of one build can change the same artifact at the same moment: evidence about the
# commit (for example the quality gate) marks every artifact of the build, while the build
# job submits evidence about one image. The Control Plane then refuses the later change with
# `409 concurrency.conflict` and stores nothing, so sending the request again is safe.
CONFLICT_ATTEMPTS = 5


def request(method: str, path: str, **kwargs) -> http.Response:
    url = f"{zone.env('SSCP_CONTROLPLANE_URL')}{path}"
    extra_headers = kwargs.pop("headers", {})
    accepted = tuple(kwargs.pop("ok", ()))
    for attempt in range(1, CONFLICT_ATTEMPTS + 1):
        headers = {"Authorization": f"Bearer {zone.controlplane_token()}", **extra_headers}
        response = http.request(method, url, headers=headers, ok=(*accepted, 409), **kwargs)
        if response.status != 409:
            return response
        if _is_concurrency_conflict(response) and attempt < CONFLICT_ATTEMPTS:
            time.sleep(attempt)
            continue
        if 409 in accepted:
            return response
        raise http.HttpError(method, url, response)
    raise AssertionError("unreachable")


def _is_concurrency_conflict(response: http.Response) -> bool:
    try:
        return (response.json() or {}).get("code") == "concurrency.conflict"
    except ValueError:
        return False


def plan(build_id: str) -> dict:
    return request("GET", f"/api/builds/{build_id}/plan").json()


def register_artifact(build_id: str, deployable: str, repository: str, digest: str) -> dict:
    return request("POST", f"/api/builds/{build_id}/artifacts",
                   json_body={"runId": zone.run_id(), "deployable": deployable, "repository": repository, "digest": digest}).json()


def submit(build_id: str, commit: str, kind: str, report: Path, *, error: str | None = None, deployable: str | None = None,
           digest: str | None = None, metadata: dict[str, str] | None = None) -> int:
    """Uploads a raw report as evidence. Returns 0 when accepted, 1 when refused.

    A scanner that failed is still reported (execution Failed, with its error), so the
    trust decision names the control that did not run instead of "evidence missing".
    """
    content = report.read_bytes() if report.exists() else b""
    fields = {
        "runId": str(zone.run_id()), "kind": kind, "execution": "Failed" if error else "Completed", "commit": commit,
        "jobName": os.environ.get("GITHUB_JOB", "unknown"), "metadata": json.dumps(metadata or {}),
    }
    for name, value in (("deployable", deployable), ("digest", digest), ("executionError", error[:1000] if error else None)):
        if value:
            fields[name] = value
    body, content_type = http.multipart(fields, {"report": (report.name, content or b"{}", "application/json")})
    response = request("POST", f"/api/builds/{build_id}/evidence", body=body, headers={"Content-Type": content_type},
                       timeout=300, ok=(400, 403, 404, 409))
    result = response.json()
    subject = f" for {deployable}" if deployable else ""
    if response.status != 201:
        print(f"{kind} evidence{subject} refused ({response.status}): {result.get('code')}: {result.get('title')}", file=sys.stderr)
        return 1
    if error:
        print(f"{kind}{subject}: scanner did not complete ({error[:200]}); recorded as Failed")
    counts = ", ".join(f"{k}={v}" for k, v in result.get("findings", {}).items()) or "no findings"
    print(f"{kind} evidence{subject} recorded: {result['id']} ({counts}); report sha256 {result['report']['sha256']}")
    return 0


def print_decision(decision: dict, label: str) -> None:
    print(f"{label}: {decision['outcome']} (policy {decision.get('policyVersion', '?')})")
    for rule in decision.get("blocking", []):
        print(f"  BLOCK {rule['rule']}: {rule['message']}")
    for rule in decision.get("warnings", []):
        print(f"  WARN  {rule['rule']}: {rule['message']}")
