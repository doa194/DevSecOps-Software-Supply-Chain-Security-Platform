"""Operational checks of observability (`sscp verify observability`, needs `--with ci,cluster`
and one deployed release).

Monitoring only helps when the signals really arrive. These checks cause real security
signals (a refused admission, rejected logins at the gateway) and look for them where a
person would: in Prometheus, in Loki through Grafana, and in the Trivy Operator's reports.
Metrics are exported about once a minute and scraped every 30 seconds, so the checks
wait a few minutes before failing.
"""
import json
import time
import uuid

import httpx
import pytest
import yaml

from sscp import cluster, credentials, paths, toolmirror

pytestmark = pytest.mark.operational

PROMETHEUS = "http://127.0.0.1:9990"
GRAFANA = "http://127.0.0.1:3300"
GATEWAY = "http://127.0.0.1:8088"
OBSERVABILITY = paths.CLUSTER_DIR / "platform" / "observability"
WORKLOADS = ["commerce-api", "commerce-gateway", "audit-worker", "reporting-worker", "notification-worker", "document-worker"]


def _query(expression: str) -> list[dict]:
    response = httpx.get(f"{PROMETHEUS}/api/v1/query", params={"query": expression}, timeout=30)
    response.raise_for_status()
    return response.json()["data"]["result"]


def _total(expression: str) -> float:
    return sum(float(sample["value"][1]) for sample in _query(expression))


def _eventually(description: str, probe, timeout: float = 240, interval: float = 10):
    deadline = time.monotonic() + timeout
    while True:
        result = probe()
        if result:
            return result
        if time.monotonic() > deadline:
            pytest.fail(f"{description}: not seen within {timeout:.0f}s")
        time.sleep(interval)


@pytest.fixture(scope="module")
def release() -> dict:
    result = cluster.kubectl("get", "configmap", "commerce-release", "-n", "commerce", "-o", "json", check=False)
    data = json.loads(result.stdout)["data"] if result.returncode == 0 else {}
    if data.get("tag", "none") == "none":
        pytest.skip("no release is deployed yet; push a release tag with `sscp repo tag`")
    return data


@pytest.fixture(scope="module")
def grafana():
    password = credentials.find("grafana.admin")
    if not password:
        pytest.skip("Grafana has no administrator password yet; run `sscp up --with ci,cluster`")
    with httpx.Client(base_url=GRAFANA, auth=("admin", password), timeout=30) as client:
        yield client


@pytest.fixture(scope="module")
def rejected_logins(release) -> str:
    """Sends requests with a forged token through the gateway; each one is a security event
    (`authentication.failed`) that must show up as a metric and as a log line."""
    before = _total(f'commerce_security_events_total{{type="authentication.failed", sscp_release_tag="{release["tag"]}"}}')
    for _ in range(5):
        response = httpx.get(f"{GATEWAY}/api/reports/summary", headers={"Authorization": "Bearer forged.token.value"}, timeout=30)
        assert response.status_code == 401
    return str(before)


# ------------------------------------------------------------------ collection

def test_every_metrics_source_is_scraped():
    targets = httpx.get(f"{PROMETHEUS}/api/v1/targets", timeout=30).json()["data"]["activeTargets"]

    jobs = {target["labels"]["job"] for target in targets}
    down = [(target["labels"]["job"], target["lastError"]) for target in targets if target["health"] != "up"]

    assert jobs >= {"workload", "kyverno", "argocd", "external-secrets", "trivy-operator", "controlplane", "harbor", "vault", "gitea"}
    assert down == []


def test_the_committed_alert_rules_are_loaded_and_evaluate():
    committed = {rule["alert"] for group in yaml.safe_load((OBSERVABILITY / "alerts.yaml").read_text(encoding="utf-8"))["groups"]
                 for rule in group["rules"]}

    groups = httpx.get(f"{PROMETHEUS}/api/v1/rules", timeout=30).json()["data"]["groups"]
    loaded = {rule["name"]: rule for group in groups for rule in group["rules"]}

    assert set(loaded) == committed
    assert [(name, rule.get("lastError")) for name, rule in loaded.items() if rule["health"] != "ok"] == []


# ------------------------------------------------------------------ workload signals

def test_workload_security_events_reach_prometheus_with_the_release_identity(release, rejected_logins):
    expression = f'commerce_security_events_total{{type="authentication.failed", sscp_release_tag="{release["tag"]}"}}'

    _eventually("the rejected logins in commerce_security_events_total", lambda: _total(expression) >= float(rejected_logins) + 5)

    series = _query(expression)
    assert {s["metric"]["sscp_release_commit"] for s in series} == {release["commit"]}
    assert {s["metric"]["k8s_namespace_name"] for s in series} == {"commerce"}
    assert all(s["metric"]["container_image_name"].startswith(f"{toolmirror.REGISTRY}/commerce-trusted/") for s in series)


def test_workload_security_logs_reach_loki_with_the_release_identity(release, rejected_logins, grafana):
    query = f'{{service_name="commerce-gateway", sscp_release_tag="{release["tag"]}"}} |= "authentication failed"'

    def lines():
        response = grafana.get("/api/datasources/proxy/uid/loki/loki/api/v1/query_range",
                               params={"query": query, "since": "15m", "limit": 50})
        response.raise_for_status()
        return [value for stream in response.json()["data"]["result"] for value in stream["values"]]

    assert _eventually("gateway security log lines in Loki", lines)


# ------------------------------------------------------------------ deployment signals

def test_refused_admissions_are_counted_per_policy(release):
    expression = 'kyverno_validating_policy_results_total{result="fail", policy_name="restrict-image-sources", execution_cause="admission_request"}'
    before = _total(expression)
    # Compliant with Pod Security (so Kyverno sees it), but the image is a tag outside the
    # trusted project: the kind of pod someone would try to start by hand.
    pod = {"apiVersion": "v1", "kind": "Pod",
           "metadata": {"name": f"observability-probe-{uuid.uuid4().hex[:6]}", "namespace": "commerce"},
           "spec": {"securityContext": {"runAsNonRoot": True, "runAsUser": 10001, "seccompProfile": {"type": "RuntimeDefault"}},
                    "containers": [{"name": "probe", "image": f"{toolmirror.REGISTRY}/platform-tools/alpine:3.22",
                                    "resources": {"requests": {"cpu": "10m", "memory": "16Mi"}, "limits": {"memory": "16Mi"}},
                                    "securityContext": {"allowPrivilegeEscalation": False, "readOnlyRootFilesystem": True,
                                                        "capabilities": {"drop": ["ALL"]}}}]}}

    refused = cluster.kubectl("apply", "--dry-run=server", "-f", "-", input_text=json.dumps(pod), check=False)

    assert refused.returncode != 0, refused.stdout
    _eventually("the refusal in kyverno_validating_policy_results_total", lambda: _total(expression) > before)


def test_running_workloads_are_scanned_for_vulnerabilities(release):
    """Every commerce workload's current ReplicaSet has a Trivy vulnerability report.
    Scans run one at a time, so right after a release this can take several minutes."""
    def unscanned():
        replica_sets = {}
        for workload in WORKLOADS:
            pods = json.loads(cluster.kubectl("get", "pods", "-n", "commerce", "-l", f"app.kubernetes.io/name={workload}", "-o", "json").stdout)["items"]
            owners = {owner["name"] for pod in pods for owner in pod["metadata"].get("ownerReferences", []) if owner["kind"] == "ReplicaSet"}
            replica_sets[workload] = owners
        reports = json.loads(cluster.kubectl("get", "vulnerabilityreports", "-n", "commerce", "-o", "json").stdout)["items"]
        scanned = {report["metadata"]["labels"].get("trivy-operator.resource.name") for report in reports}
        return [workload for workload, owners in replica_sets.items() if not owners or not owners <= scanned]

    _eventually("vulnerability reports for every workload", lambda: unscanned() == [], timeout=900, interval=30)
    assert _query('trivy_image_vulnerabilities{namespace="commerce"}')


# ------------------------------------------------------------------ presentation

def test_grafana_reaches_its_datasources_and_serves_the_committed_dashboards(grafana):
    committed = {json.loads(path.read_text(encoding="utf-8"))["uid"] for path in (OBSERVABILITY / "dashboards").glob("*.json")}

    health = {uid: grafana.get(f"/api/datasources/uid/{uid}/health").json().get("status") for uid in ("prometheus", "loki")}
    served = {board["uid"] for board in grafana.get("/api/search", params={"type": "dash-db"}).json()}

    assert health == {"prometheus": "OK", "loki": "OK"}
    assert committed <= served
