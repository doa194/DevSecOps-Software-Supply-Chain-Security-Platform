"""The kind cluster: creation, registry trust and the tools used to operate it.

The cluster is the deployment target. The bootstrap creates it, installs the platform
add-ons (Argo CD, Kyverno, External Secrets Operator) and hands over to Argo CD; from then
on every change arrives through Git. CI never receives cluster credentials: the kubeconfig
lives in .local/generated and is used only by this automation and the operational tests.
"""
from __future__ import annotations

import hashlib
import json
import os
import time
import urllib.request
from pathlib import Path

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

from sscp import credentials, docker, paths, pki, shell, signing, sourcecontrol, versions, workload
from sscp.services import keycloak, vault

NAME = "sscp"
NODE = f"{NAME}-control-plane"
NETWORK = "sscp-edge"
KUBECONFIG = paths.GENERATED_DIR / "kubeconfig"
HELM_HOME = paths.LOCAL / "helm"
REGISTRY = "harbor.sscp.test:8443"
CA_IN_NODE = "/etc/sscp/ca.crt"


# ------------------------------------------------------------------ tools

def kind_binary() -> str:
    """kind from .tools/bin, downloaded once and accepted only with the pinned SHA-256."""
    spec = versions.tool("kind")
    target = paths.TOOLS_BIN / "kind.exe" if os.name == "nt" else paths.TOOLS_BIN / "kind"
    if target.exists() and _sha256(target) == spec["sha256"]:
        return str(target)
    paths.TOOLS_BIN.mkdir(parents=True, exist_ok=True)
    download = target.with_suffix(".download")
    with urllib.request.urlopen(spec["url"], timeout=300) as response:
        download.write_bytes(response.read())
    if _sha256(download) != spec["sha256"]:
        download.unlink()
        raise RuntimeError(f"kind {spec['version']} download does not match the pinned SHA-256; refusing to use it")
    os.replace(download, target)
    target.chmod(0o755)
    return str(target)


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def environment() -> dict[str, str]:
    """kubectl and Helm use only the cluster's own kubeconfig and a Helm home inside
    .local, never the workstation's personal configuration."""
    return {
        "KUBECONFIG": str(KUBECONFIG),
        "HELM_CONFIG_HOME": str(HELM_HOME / "config"),
        "HELM_CACHE_HOME": str(HELM_HOME / "cache"),
        "HELM_DATA_HOME": str(HELM_HOME / "data"),
    }


def kubectl(*args: str, check: bool = True, input_text: str | None = None, timeout: float = 300):
    return shell.run(["kubectl", *args], env=environment(), check=check, input_text=input_text, timeout=timeout)


def helm(*args: str, timeout: float = 900):
    return shell.run(["helm", *args], env=environment(), timeout=timeout)


def apply(manifest: str | dict | list) -> None:
    """Server-side applies manifests given as YAML text or objects."""
    text = manifest if isinstance(manifest, str) else json.dumps(
        {"apiVersion": "v1", "kind": "List", "items": manifest} if isinstance(manifest, list) else manifest)
    kubectl("apply", "--server-side", "--force-conflicts", "--field-manager", "sscp-bootstrap", "-f", "-", input_text=text)


# ------------------------------------------------------------------ cluster

def exists() -> bool:
    result = shell.run([kind_binary(), "get", "clusters"], check=False, timeout=60)
    return NAME in result.stdout.split()


def node_exists() -> bool:
    return docker.container_state(NODE) is not None


def stop() -> None:
    shell.run(["docker", "stop", NODE], timeout=300)


def create() -> None:
    """Creates the cluster on the platform network, or restarts a stopped one (idempotent)."""
    state = docker.container_state(NODE)
    if state is not None and exists():
        if not state.get("Running"):
            shell.run(["docker", "start", NODE], timeout=300)
        _export_kubeconfig()
        wait_until(lambda: kubectl("get", "--raw", "/readyz", check=False).returncode == 0, "the Kubernetes API", timeout=300)
        return
    config = paths.CLUSTER_DIR / "kind.yaml"
    # kind attaches the node to this existing network instead of creating its own.
    env = {"KIND_EXPERIMENTAL_DOCKER_NETWORK": NETWORK}
    shell.run([kind_binary(), "create", "cluster", "--config", str(config), "--image", versions.load()["kubernetes"]["nodeImage"],
               "--kubeconfig", str(KUBECONFIG), "--wait", "180s"], env=env, timeout=900)


def _export_kubeconfig() -> None:
    shell.run([kind_binary(), "export", "kubeconfig", "--name", NAME, "--kubeconfig", str(KUBECONFIG)], timeout=60)


def delete() -> None:
    if exists():
        shell.run([kind_binary(), "delete", "cluster", "--name", NAME], timeout=300)
    KUBECONFIG.unlink(missing_ok=True)


def trust_registry() -> None:
    """Lets the node's container runtime pull from Harbor over TLS with the platform CA.

    containerd reads per-registry settings from /etc/containerd/certs.d/<host:port>/;
    the directory name contains a colon, so it is created inside the node rather than
    mounted from Windows.
    """
    docker.exec_in(NODE, ["mkdir", "-p", "/etc/sscp", f"/etc/containerd/certs.d/{REGISTRY}"])
    shell.run(["docker", "cp", str(pki.CA_CERT.resolve()), f"{NODE}:{CA_IN_NODE}"])
    hosts = (f'server = "https://{REGISTRY}"\n\n'
             f'[host."https://{REGISTRY}"]\n'
             f'  capabilities = ["pull", "resolve"]\n'
             f'  ca = "{CA_IN_NODE}"\n')
    docker.exec_in(NODE, ["sh", "-c", f"cat > /etc/containerd/certs.d/{REGISTRY}/hosts.toml"], input_text=hosts)


def node_address() -> str:
    inspect = shell.output(["docker", "inspect", NODE, "--format", "{{json .NetworkSettings.Networks}}"])
    return json.loads(inspect)[NETWORK]["IPAddress"]


def wait_for(kind: str, name: str, namespace: str, condition: str = "Available", timeout: int = 600) -> None:
    kubectl("wait", f"{kind}/{name}", "-n", namespace, f"--for=condition={condition}", f"--timeout={timeout}s", timeout=timeout + 30)


def wait_rollout(kind: str, name: str, namespace: str, timeout: int = 600) -> None:
    kubectl("rollout", "status", f"{kind}/{name}", "-n", namespace, f"--timeout={timeout}s", timeout=timeout + 30)


def wait_until(check, description: str, timeout: float = 300, interval: float = 5):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = check()
        if result:
            return result
        time.sleep(interval)
    raise TimeoutError(f"timed out waiting for {description}")


# ------------------------------------------------------------------ add-ons

# Namespaces of the platform add-ons. Application namespaces are declared in
# cluster/platform/namespaces.yaml and reach the cluster through Argo CD.
ADDON_NAMESPACES = ["external-secrets", "kyverno", "argocd", "trivy-system"]
ADDONS = [
    # (release name, versions.yaml chart key, namespace, values file)
    ("external-secrets", "externalSecrets", "external-secrets", "external-secrets.yaml"),
    ("kyverno", "kyverno", "kyverno", "kyverno.yaml"),
    ("argocd", "argocd", "argocd", "argocd.yaml"),
    ("trivy-operator", "trivyOperator", "trivy-system", "trivy-operator.yaml"),
]


def ca_configmap(namespace: str) -> dict:
    """The platform root CA as a ConfigMap. It is generated per installation, so it
    cannot come from Git; it is public, so a ConfigMap (not a Secret) is right."""
    return {
        "apiVersion": "v1", "kind": "ConfigMap",
        "metadata": {"name": "sscp-root-ca", "namespace": namespace, "labels": {"app.kubernetes.io/part-of": "sscp"}},
        "data": {"ca.crt": pki.CA_CERT.read_text(encoding="utf-8")},
    }


def install_addons() -> None:
    namespaces = [{"apiVersion": "v1", "kind": "Namespace", "metadata": {"name": name}} for name in ADDON_NAMESPACES]
    apply(namespaces + [ca_configmap(name) for name in ADDON_NAMESPACES])
    ca = pki.CA_CERT.resolve().as_posix()  # Helm reads backslashes as escapes
    extra = {
        "kyverno": ["--set-file", f"global.caCertificates.data={ca}"],
        # Dots inside a Helm key are escaped so the host name stays one key.
        "argocd": ["--set-file", rf"configs.tls.certificates.gitea\.sscp\.test={ca}"],
    }
    for release, key, namespace, values in ADDONS:
        chart = versions.chart(key)
        helm("upgrade", "--install", release, chart["chart"], "--repo", chart["repository"], "--version", chart["version"],
             "--namespace", namespace, "--values", str(paths.CLUSTER_DIR / "helm" / values), *extra.get(release, []),
             "--wait", "--timeout", "10m")


# ------------------------------------------------------------------ Vault and secrets

JWT_MOUNT = "jwt-kind"
TOKEN_ISSUER = "https://kubernetes.default.svc.cluster.local"
# Vault role per namespace -> the KV paths External Secrets may read for that namespace.
SECRET_SCOPES = {
    "commerce": ["kv/data/workload/commerce/*", "kv/data/platform/cluster/harbor-pull"],
    "commerce-data": ["kv/data/workload/commerce-data/*"],
    "argocd": ["kv/data/platform/cluster/argocd/*"],
    "kyverno": ["kv/data/platform/cluster/harbor-pull"],
    "observability": ["kv/data/platform/cluster/observability/*"],
}
DATA_HOSTS = {name: f"{name}.commerce-data.svc.cluster.local" for name in ("postgres", "redis", "rabbitmq", "minio")}


def configure_vault() -> None:
    """Lets External Secrets authenticate to Vault with the cluster's service-account tokens.

    Vault checks each token's signature offline with the cluster's service-account public
    key, so Vault needs no credentials for the cluster and no network path into it. Each
    namespace's `vault-secrets` account maps to a role that can read only that namespace's
    paths.
    """
    client = vault.bootstrap_client()
    if f"{JWT_MOUNT}/" not in client.read("sys/auth")["data"]:
        client.write(f"sys/auth/{JWT_MOUNT}", {"type": "jwt", "description": "Service-account tokens of the kind cluster"})
    public_key = docker.exec_in(NODE, ["cat", "/etc/kubernetes/pki/sa.pub"]).stdout
    client.write(f"auth/{JWT_MOUNT}/config", {"jwt_validation_pubkeys": [public_key], "bound_issuer": TOKEN_ISSUER})
    for namespace, readable in SECRET_SCOPES.items():
        policy = "".join(f'path "{path}" {{ capabilities = ["read"] }}\n' for path in readable)
        client.request("PUT", f"sys/policies/acl/cluster-{namespace}", json={"policy": policy})
        client.write(f"auth/{JWT_MOUNT}/role/eso-{namespace}", {
            "role_type": "jwt", "user_claim": "sub", "bound_audiences": ["vault.sscp.test"],
            "bound_subject": f"system:serviceaccount:{namespace}:vault-secrets",
            "token_policies": [f"cluster-{namespace}"], "token_ttl": "10m", "token_max_ttl": "15m",
        })
    for path, data in {**workload_secrets(), **platform_secrets()}.items():
        client.write(f"kv/data/{path}", {"data": data})


def _publisher_key(publisher: str) -> str:
    """ECDSA P-256 private key (PEM) a service signs its integration events with."""
    def generate() -> str:
        key = ec.generate_private_key(ec.SECP256R1())
        return key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                 serialization.NoEncryption()).decode("ascii")
    return credentials.get(f"cluster.signing.{publisher}", generate)


def _public_pem(private_pem: str) -> str:
    key = serialization.load_pem_private_key(private_pem.encode("ascii"), password=None)
    return key.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode("ascii")


def _database_url(role: str) -> str:
    return f"Host={DATA_HOSTS['postgres']};Database=commerce;Username={role};Password={credentials.get(f'cluster.db.{role}')}"


def workload_secrets() -> dict[str, dict[str, str]]:
    """Runtime secrets of the commerce services and their data services, keyed by KV path.

    Values are generated once per installation and kept in the local credential store,
    so rerunning the bootstrap writes the same values again.
    """
    publishers = [s.name for s in workload.SERVICES if s.publisher]
    public_keys = {f"Messaging__TrustedPublishers__{p}-key__PublicKeyPem": _public_pem(_publisher_key(p)) for p in publishers}
    rabbit = credentials.get("cluster.rabbitmq")
    minio = credentials.get("cluster.minio")
    secrets: dict[str, dict[str, str]] = {}
    for service in workload.SERVICES:
        if service.name == "commerce-gateway":
            continue  # the gateway holds no secrets
        data = {"Messaging__ConnectionString": f"amqp://commerce:{rabbit}@{DATA_HOSTS['rabbitmq']}:5672/commerce", **public_keys}
        for schema in service.schemas:
            data[f"ConnectionStrings__{schema}"] = _database_url(workload.role_for(schema))
        if service.publisher:
            data["Messaging__Signing__PrivateKeyPem"] = _publisher_key(service.name)
        if service.redis:
            data["ConnectionStrings__redis"] = f"{DATA_HOSTS['redis']}:6379,password={credentials.get('cluster.redis')},abortConnect=false"
        if service.storage:
            data |= {"ObjectStorage__AccessKey": "commerce-admin", "ObjectStorage__SecretKey": minio}
        if service.name == "commerce-api":
            data["Identity__Admin__ClientSecret"] = keycloak.client_secret("commerce", "commerce-identity-admin")
        secrets[f"workload/commerce/{service.name}"] = data
    # Only the migrations Job gets the schema owner; running services use per-module roles.
    secrets["workload/commerce/commerce-migrations"] = {"ConnectionStrings__migrations": _database_url("commerce_owner")}

    roles = ["commerce_owner", *[workload.role_for(s) for s in workload.API_SCHEMAS], *workload.WORKER_ROLES.values()]
    init = [f"CREATE ROLE {role} LOGIN PASSWORD '{credentials.get(f'cluster.db.{role}')}';\nGRANT CONNECT ON DATABASE commerce TO {role};"
            for role in roles]
    init += ["GRANT CREATE ON DATABASE commerce TO commerce_owner;", "REVOKE CREATE ON SCHEMA public FROM PUBLIC;"]
    secrets["workload/commerce-data/postgres"] = {"POSTGRES_PASSWORD": credentials.get("cluster.db.postgres"), "init.sql": "\n".join(init) + "\n"}
    secrets["workload/commerce-data/redis"] = {"REDIS_PASSWORD": credentials.get("cluster.redis")}
    secrets["workload/commerce-data/rabbitmq"] = {"RABBITMQ_DEFAULT_USER": "commerce", "RABBITMQ_DEFAULT_PASS": rabbit}
    secrets["workload/commerce-data/minio"] = {"MINIO_ROOT_USER": "commerce-admin", "MINIO_ROOT_PASSWORD": minio}
    return secrets


def platform_secrets() -> dict[str, dict[str, str]]:
    """Credentials of Argo CD, the observability stack and the release bot. The pull-only
    registry robot is written by registry.configure()."""
    return {
        "platform/cluster/argocd/repositories": {"username": "sscp-argocd",
                                                 "token": sourcecontrol.user_token("sscp-argocd", ["read:repository"])},
        "platform/cluster/argocd/notifications": {"controlplane-token": credentials.get("argocd.notifications-token", credentials.hex_token)},
        "platform/cluster/observability/grafana": {"admin-password": credentials.get("grafana.admin")},
        "platform/cluster/observability/gitea-metrics": {"token": credentials.get("gitea.metrics-token")},
        # Readable only with a signing grant (Vault policy trust-signer).
        "ci/trust-signer/gitops": {"token": sourcecontrol.user_token("sscp-gitops-bot", ["write:repository"]),
                                   "repository": "platform/commerce-gitops"},
    }


# ------------------------------------------------------------------ hand-over to Argo CD

# Namespaces from cluster/platform/namespaces.yaml that need the platform CA before Argo CD
# takes over (their secret stores and scrape targets use TLS with the local CA).
PLATFORM_NAMESPACES = ["commerce", "commerce-data", "observability"]


def install_platform_state() -> None:
    """Applies the platform's cluster state once and hands it to Argo CD.

    From then on Argo CD keeps the cluster in line with cluster/platform on the platform
    repository's main branch, and with the GitOps repository for the workload.
    """
    server_side = ["apply", "--server-side", "--force-conflicts", "--field-manager", "sscp-bootstrap"]
    kubectl(*server_side, "-f", str(paths.CLUSTER_DIR / "platform" / "namespaces.yaml"))
    # Installation trust anchors, generated per installation and therefore not in Git:
    # the platform CA and the public half of the release signing key.
    apply([ca_configmap(namespace) for namespace in PLATFORM_NAMESPACES] + [{
        "apiVersion": "v1", "kind": "ConfigMap",
        "metadata": {"name": "cosign-commerce", "namespace": "kyverno", "labels": {"app.kubernetes.io/part-of": "sscp"}},
        "data": {"key.pem": signing.PUBLIC_KEY_FILE.read_text(encoding="utf-8")},
    }])
    kubectl(*server_side, "-k", str(paths.CLUSTER_DIR / "platform"))
    kubectl(*server_side, "-f", str(paths.CLUSTER_DIR / "bootstrap" / "platform.yaml"))
