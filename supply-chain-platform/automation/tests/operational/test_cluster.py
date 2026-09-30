"""Operational checks of the deployment target (`sscp verify cluster`, needs `--with ci,cluster`
and one deployed release).

Kubernetes must enforce trust independently of CI: these checks send real requests to the
cluster and look at what it refuses. Admission checks use server-side dry runs, so they
pass through every admission layer (Pod Security, Kyverno, native policies) without
creating anything.
"""
import copy
import json
import time
import uuid

import pytest
import yaml

from sscp import cluster, http, paths, probe, registry, toolmirror
from sscp.services import controlplane, harbor

pytestmark = pytest.mark.operational

TRUSTED = f"{toolmirror.REGISTRY}/{registry.TRUSTED}"


def _kubectl(*args: str, input_text: str | None = None):
    return cluster.kubectl(*args, check=False, input_text=input_text)


def _admitted(manifest: dict) -> tuple[bool, str]:
    result = _kubectl("apply", "--dry-run=server", "-f", "-", input_text=json.dumps(manifest))
    return result.returncode == 0, result.stdout + result.stderr


def _json(*args: str) -> dict:
    return json.loads(cluster.kubectl(*args, "-o", "json").stdout)


@pytest.fixture(scope="module")
def api_pod() -> dict:
    """A pod exactly like the running commerce API: signed image, hardened settings."""
    result = _kubectl("get", "deployment", "commerce-api", "-n", "commerce", "-o", "json")
    if result.returncode != 0:
        pytest.skip("no release is deployed yet; push a release tag with `sscp repo tag`")
    template = json.loads(result.stdout)["spec"]["template"]
    return {"apiVersion": "v1", "kind": "Pod",
            "metadata": {"name": "admission-probe", "namespace": "commerce", "labels": template["metadata"]["labels"]},
            "spec": template["spec"]}


def _variant(pod: dict, change) -> dict:
    changed = copy.deepcopy(pod)
    change(changed["spec"])
    return changed


# ------------------------------------------------------------------ admission: images

def test_the_signed_release_image_is_admitted(api_pod):
    admitted, output = _admitted(api_pod)

    assert admitted, output


def test_an_unsigned_image_pushed_into_the_trusted_project_is_refused(api_pod):
    repository = f"{TRUSTED}/admission-probe-{uuid.uuid4().hex[:6]}"
    toolmirror.registry_tool("crane", ["copy", toolmirror.references()["alpine"], f"{repository}:probe"], timeout=120)
    digest = toolmirror.registry_tool("crane", ["digest", f"{repository}:probe"], timeout=60)
    try:
        pod = _variant(api_pod, lambda spec: spec["containers"][0].update(image=f"{repository}@{digest}"))

        admitted, output = _admitted(pod)

        assert not admitted and "not signed with the commerce release key" in output, output
    finally:
        harbor.HarborApi().request("DELETE", f"projects/{registry.TRUSTED}/repositories/{repository.rsplit('/', 1)[1]}")


@pytest.mark.parametrize("reference, reason", [
    # The same digest, but from the candidates project: approved location only.
    ("candidate", "must come from harbor.sscp.test:8443/commerce-trusted/"),
    # The release tag instead of the digest: tags are never an image's identity.
    ("tag", "pinned by digest"),
])
def test_images_outside_the_trusted_project_or_by_tag_are_refused(api_pod, reference, reason):
    image = api_pod["spec"]["containers"][0]["image"]
    repository, digest = image.split("@")
    tag = json.loads(_kubectl("get", "configmap", "commerce-release", "-n", "commerce", "-o", "json").stdout)["data"]["tag"]
    replacement = {"candidate": repository.replace(registry.TRUSTED, registry.CANDIDATES) + "@" + digest,
                   "tag": f"{repository}:{tag}"}[reference]
    pod = _variant(api_pod, lambda spec: spec["containers"][0].update(image=replacement))

    admitted, output = _admitted(pod)

    assert not admitted and reason in output, output


# ------------------------------------------------------------------ admission: workload settings

INSECURE_POD_CHANGES = {
    "privileged": lambda spec: spec["containers"][0]["securityContext"].update(privileged=True, allowPrivilegeEscalation=True),
    "root user": lambda spec: spec["securityContext"].update(runAsNonRoot=False, runAsUser=0),
    "host path": lambda spec: spec["volumes"].append({"name": "host", "hostPath": {"path": "/etc"}}),
    "writable root file system": lambda spec: spec["containers"][0]["securityContext"].update(readOnlyRootFilesystem=False),
    "service account token": lambda spec: spec.update(automountServiceAccountToken=True),
    "default service account": lambda spec: spec.update(serviceAccountName="default"),
    "added capability": lambda spec: spec["containers"][0]["securityContext"]["capabilities"].update(add=["NET_RAW"]),
}


@pytest.mark.parametrize("setting", sorted(INSECURE_POD_CHANGES))
def test_insecure_pod_settings_are_refused(api_pod, setting):
    admitted, output = _admitted(_variant(api_pod, INSECURE_POD_CHANGES[setting]))

    assert not admitted, f"{setting} was admitted"


def test_workloads_without_resource_limits_are_refused():
    deployment = _json("get", "deployment", "commerce-api", "-n", "commerce")
    for key in ("status", "managedFields", "resourceVersion", "uid", "creationTimestamp", "generation", "annotations"):
        deployment["metadata"].pop(key, None)
    deployment.pop("status", None)
    deployment["spec"]["template"]["spec"]["containers"][0]["resources"].pop("limits", None)

    admitted, output = _admitted(deployment)

    assert not admitted and "memory limit" in output, output


def test_new_external_exposure_is_refused():
    service = {"apiVersion": "v1", "kind": "Service", "metadata": {"name": "exposure-probe", "namespace": "commerce"},
               "spec": {"type": "NodePort", "selector": {"app.kubernetes.io/name": "commerce-api"}, "ports": [{"port": 8080}]}}

    admitted, output = _admitted(service)

    assert not admitted and "only ClusterIP Services" in output, output


def test_secrets_in_application_namespaces_come_only_from_external_secrets():
    result = _kubectl("create", "secret", "generic", "handmade", "-n", "commerce", "--from-literal=password=plaintext", "--dry-run=server")

    assert result.returncode != 0 and "managed by External Secrets" in result.stderr, result.stderr
    owner = _json("get", "secret", "commerce-api", "-n", "commerce")["metadata"]["ownerReferences"][0]
    assert (owner["kind"], owner["name"]) == ("ExternalSecret", "commerce-api")


def test_debug_containers_are_refused(api_pod):
    running = _json("get", "pods", "-n", "commerce", "-l", "app.kubernetes.io/name=commerce-api")["items"][0]["metadata"]["name"]
    debug = {"spec": {"ephemeralContainers": [{
        "name": "debug", "image": toolmirror.references()["alpine"], "command": ["sh"],
        "securityContext": {"allowPrivilegeEscalation": False, "runAsNonRoot": True, "runAsUser": 1000,
                            "capabilities": {"drop": ["ALL"]}, "seccompProfile": {"type": "RuntimeDefault"}},
    }]}}

    result = _kubectl("patch", "pod", running, "-n", "commerce", "--subresource=ephemeralcontainers", "--type=strategic",
                      "--dry-run=server", "-p", json.dumps(debug))

    assert result.returncode != 0 and "ephemeral (debug) containers are not allowed" in result.stderr, result.stderr


# ------------------------------------------------------------------ network

PROBE_NAMESPACE = "sscp-netprobe"
CONNECT = """
import json, socket, sys
results = {}
for name, host, port in json.loads(sys.argv[1]):
    try:
        socket.create_connection((host, port), timeout=3).close()
        results[name] = True
    except OSError:
        results[name] = False
print(json.dumps(results))
"""


def _pod(name: str, namespace: str, labels: dict, args: list[str]) -> dict:
    return {"apiVersion": "v1", "kind": "Pod", "metadata": {"name": name, "namespace": namespace, "labels": labels},
            "spec": {"restartPolicy": "Never", "automountServiceAccountToken": False,
                     "containers": [{"name": "probe", "image": toolmirror.references()["python"], "args": args,
                                     "resources": {"limits": {"memory": "64Mi"}}}]}}


def _connect_from(namespace: str, labels: dict, targets: list[tuple[str, str, int]]) -> dict:
    name = f"client-{uuid.uuid4().hex[:6]}"
    cluster.apply(_pod(name, namespace, labels, ["python", "-c", CONNECT, json.dumps(targets)]))
    cluster.kubectl("wait", f"pod/{name}", "-n", namespace, "--for=jsonpath={.status.phase}=Succeeded", "--timeout=120s", timeout=150)
    output = cluster.kubectl("logs", name, "-n", namespace).stdout
    _kubectl("delete", "pod", name, "-n", namespace, "--wait=false")
    return json.loads(output.strip().splitlines()[-1])


@pytest.fixture(scope="module")
def probe_namespace():
    cluster.apply({"apiVersion": "v1", "kind": "Namespace", "metadata": {"name": PROBE_NAMESPACE}})
    yield PROBE_NAMESPACE
    _kubectl("delete", "namespace", PROBE_NAMESPACE, "--wait=false")


def test_from_outside_only_the_gateway_is_reachable(api_pod, probe_namespace):
    targets = [
        ("gateway", "commerce-gateway.commerce.svc.cluster.local", 8080),
        ("api", "commerce-api.commerce.svc.cluster.local", 8080),
        ("postgres", "postgres.commerce-data.svc.cluster.local", 5432),
        ("redis", "redis.commerce-data.svc.cluster.local", 6379),
    ]

    reached = _connect_from(probe_namespace, {"app.kubernetes.io/name": "probe"}, targets)

    assert reached == {"gateway": True, "api": False, "postgres": False, "redis": False}


@pytest.fixture(scope="module")
def replica():
    """The application namespaces' exact network policies, applied to two throw-away
    namespaces where probe pods can play every workload role."""
    names = {"commerce": "sscp-np-commerce", "commerce-data": "sscp-np-commerce-data"}
    policies = yaml.safe_load((paths.CLUSTER_DIR / "platform" / "network-policies.yaml").read_text(encoding="utf-8"))["items"]

    def rename(node):
        if isinstance(node, dict):
            return {k: (names.get(v, v) if k in ("namespace", "kubernetes.io/metadata.name") and isinstance(v, str) else rename(v))
                    for k, v in node.items()}
        return [rename(v) for v in node] if isinstance(node, list) else node

    cluster.apply([{"apiVersion": "v1", "kind": "Namespace", "metadata": {"name": n}} for n in names.values()] + [rename(p) for p in policies])
    servers = {"postgres": 5432, "redis": 6379, "minio": 9000, "commerce-api": 8080}
    for server, port in servers.items():
        namespace = names["commerce"] if server == "commerce-api" else names["commerce-data"]
        cluster.apply(_pod(f"server-{server}", namespace, {"app.kubernetes.io/name": server}, ["python", "-m", "http.server", str(port)]))
    addresses = {}
    for server in servers:
        namespace = names["commerce"] if server == "commerce-api" else names["commerce-data"]
        cluster.kubectl("wait", f"pod/server-{server}", "-n", namespace, "--for=condition=Ready", "--timeout=120s", timeout=150)
        addresses[server] = _json("get", "pod", f"server-{server}", "-n", namespace)["status"]["podIP"]
    time.sleep(3)  # let the network policy agent see the new pods
    yield names, {server: (addresses[server], port) for server, port in servers.items()}
    for namespace in names.values():
        _kubectl("delete", "namespace", namespace, "--wait=false")


@pytest.mark.parametrize("client, expected", [
    ("commerce-gateway", {"commerce-api": True, "postgres": False, "redis": False, "minio": False}),
    ("commerce-api", {"commerce-api": False, "postgres": True, "redis": True, "minio": True}),
    ("reporting-worker", {"commerce-api": False, "postgres": True, "redis": False, "minio": False}),
    ("document-worker", {"commerce-api": False, "postgres": True, "redis": False, "minio": True}),
])
def test_east_west_traffic_follows_the_declared_paths(replica, client, expected):
    names, servers = replica
    targets = [(server, address, port) for server, (address, port) in servers.items()]

    reached = _connect_from(names["commerce"], {"app.kubernetes.io/name": client}, targets)

    assert reached == expected


# ------------------------------------------------------------------ identities and secrets

WORKLOADS = ["commerce-api", "commerce-gateway", "audit-worker", "reporting-worker", "notification-worker", "document-worker"]


@pytest.mark.parametrize("verb, resource", [("list", "pods"), ("get", "secrets"), ("create", "deployments")])
def test_workload_identities_have_no_kubernetes_api_access(verb, resource):
    for workload in WORKLOADS:
        answer = _kubectl("auth", "can-i", verb, resource, "-n", "commerce", f"--as=system:serviceaccount:commerce:{workload}")
        assert answer.stdout.strip() == "no", f"{workload} may {verb} {resource}"


def test_running_pods_carry_no_service_account_token():
    for pod in _json("get", "pods", "-n", "commerce")["items"]:
        volumes = [v for v in pod["spec"].get("volumes", []) if "projected" in v or v["name"].startswith("kube-api-access")]
        assert pod["spec"].get("automountServiceAccountToken") is False and not volumes, pod["metadata"]["name"]


def test_a_namespace_cannot_read_another_namespaces_secrets_from_vault():
    probe_secret = {
        "apiVersion": "external-secrets.io/v1", "kind": "ExternalSecret",
        "metadata": {"name": "scope-probe", "namespace": "commerce"},
        "spec": {"refreshInterval": "1m", "secretStoreRef": {"kind": "SecretStore", "name": "vault"},
                 "target": {"name": "scope-probe"},
                 "data": [{"secretKey": "password", "remoteRef": {"key": "workload/commerce-data/postgres", "property": "POSTGRES_PASSWORD"}}]},
    }
    cluster.apply(probe_secret)
    try:
        def condition():
            status = _json("get", "externalsecret", "scope-probe", "-n", "commerce").get("status", {})
            return next((c for c in status.get("conditions", []) if c["type"] == "Ready"), None)
        ready = cluster.wait_until(condition, "the probe ExternalSecret to be evaluated", timeout=90, interval=3)

        assert ready["status"] == "False"
        assert _kubectl("get", "secret", "scope-probe", "-n", "commerce").returncode != 0
    finally:
        _kubectl("delete", "externalsecret", "scope-probe", "-n", "commerce")


# ------------------------------------------------------------------ deployment path

def test_ci_zones_have_no_way_into_the_cluster_api():
    script = f"""
import json, ssl, urllib.request, urllib.error
context = ssl._create_unverified_context()  # the probe only wants the status code
try:
    urllib.request.urlopen("https://{cluster.node_address()}:6443/api/v1/namespaces/commerce/secrets", context=context, timeout=10)
    status = 200
except urllib.error.HTTPError as error:
    status = error.code
print(json.dumps({{"status": status}}))
"""
    assert probe.run_in_zone("trust", script)["status"] in (401, 403)


def test_the_commerce_project_cannot_deploy_outside_its_namespaces():
    application = {
        "apiVersion": "argoproj.io/v1alpha1", "kind": "Application",
        "metadata": {"name": "project-probe", "namespace": "argocd"},
        "spec": {"project": "commerce",
                 "source": {"repoURL": "https://gitea.sscp.test:3000/platform/commerce-gitops.git", "targetRevision": "main", "path": "overlays/local"},
                 "destination": {"server": "https://kubernetes.default.svc", "namespace": "kube-system"}},
    }
    cluster.apply(application)
    try:
        def invalid():
            conditions = _json("get", "application", "project-probe", "-n", "argocd").get("status", {}).get("conditions", [])
            return next((c for c in conditions if c["type"] == "InvalidSpecError"), None)

        assert "do not match any of the allowed destinations" in cluster.wait_until(invalid, "Argo CD to evaluate the probe", timeout=90, interval=3)["message"]
    finally:
        _kubectl("delete", "application", "project-probe", "-n", "argocd")


def test_the_running_release_is_traceable_from_digest_to_cluster(api_pod):
    digest = api_pod["spec"]["containers"][0]["image"].split("@")[1]
    revision = _json("get", "application", "commerce", "-n", "argocd")["status"]["sync"]["revision"]
    with http.client("controlplane") as client:
        trace = client.get(f"/api/artifacts/{digest}/trace", headers={"Authorization": f"Bearer {controlplane.user_token('victor')}"}).json()

    assert any(entry["artifact"]["state"] == "Deployed" and any(d["gitOpsRevision"] == revision for d in entry["deployments"]) for entry in trace)
