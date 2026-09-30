"""Start, stop, reset and inspect the software factory (`sscp up|down|reset|status`).

`up` is idempotent: running it again converges the platform to the configured state
(starting stopped containers, unsealing Vault after a restart, re-applying
configuration) instead of failing because something already exists.
"""
from __future__ import annotations

import shutil

from sscp import cizones, cluster, compose, console, docker, http, paths, pki, registry, signing, sourcecontrol, toolmirror, vulndb
from sscp.services import controlplane, gitea, harbor, keycloak, minio, sonarqube, vault

# Optional capability groups. Core services (PostgreSQL, Vault, Keycloak, MinIO, Gitea)
# always run; Harbor and the Security Control Plane are part of the default foundation
# because every later stage needs them.
OPTIONAL = ("quality", "workload", "ci", "cluster")
DEFAULT = ("registry", "controlplane")
# Every Compose profile, so `down` and `reset` also reach services started on demand.
ALL_PROFILES = ["quality", "workload", "ci", "controlplane"]


def _start_core() -> None:
    with console.step("start core services (PostgreSQL, Vault, Keycloak, MinIO, Gitea)"):
        compose.compose("up", "-d", "--wait", "--wait-timeout", "600", timeout=900)
    with console.step("initialise and unseal Vault; configure engines and audit device"):
        vault.bootstrap()
    with console.step("create the Gitea bootstrap administrator"):
        gitea.wait_ready()
        gitea.ensure_admin()
    with console.step("apply source control: people, organisations, repositories, protections, webhook"):
        sourcecontrol.apply()
    with console.step("create the object-locked evidence bucket and its least-privilege user"):
        minio.wait_ready()
        minio.configure()
    with console.step("apply the Keycloak realms: `platform` (people, CI zone clients) and `commerce` (workload)"):
        keycloak.wait_ready()
        keycloak.apply_realm("platform")
        keycloak.apply_realm("commerce")


def _start_controlplane() -> None:
    with console.step("build the Security Control Plane image (first build takes a few minutes)"):
        controlplane.build()
    with console.step("prepare release signing in Vault (Transit key, trust-signer and grant-issuing identities)"):
        signing.configure()
    with console.step("create the Control Plane runtime database role and apply migrations"):
        controlplane.ensure_runtime_role()
        controlplane.migrate()
    with console.step("start the Security Control Plane"):
        sourcecontrol.controlplane_token()
        compose.write_env_file()  # now includes the Control Plane's Gitea token
        controlplane.start()


def _start_ci() -> None:
    with console.step("mirror pinned CI tool images into Harbor (the first run downloads several GB)"):
        toolmirror.mirror()
    with console.step("refresh the offline vulnerability databases (Trivy, Grype) in Harbor"):
        vulndb.refresh()
    with console.step("create Harbor projects for candidates and trusted images, and zone robot accounts"):
        registry.configure()
    with console.step("build and publish the sonar-dotnet and ci-tools images"):
        toolmirror.publish_sonar_dotnet()
        ci_tools = toolmirror.publish_ci_tools()
    with console.step("create CI zone identities in Vault (AppRoles bound to runner addresses)"):
        cizones.configure_vault()
    with console.step("configure and start the four zone runners"):
        cizones.start(cizones.write_configs(ci_tools))


def _start_registry() -> None:
    with console.step("generate the Harbor deployment with Harbor's own generator"):
        harbor.generate()
    with console.step("start Harbor"):
        # No `--wait`: Harbor's jobservice restarts once while core is still starting,
        # which Compose would report as a failure. Harbor's health API is the real signal.
        harbor.compose("up", "-d", timeout=900)
        harbor.fix_volume_ownership()
        harbor.refresh_proxy_if_stale()
        http.wait_for(
            "Harbor",
            lambda: http.client("harbor").get("/api/v2.0/health").json().get("status") == "healthy",
            timeout=300,
        )


def _start_quality() -> None:
    with console.step("start SonarQube (can take a few minutes)"):
        compose.compose("up", "-d", "sonarqube", profiles=["quality"], timeout=600)
        sonarqube.wait_ready()
        sonarqube.rotate_default_password()
    with console.step("configure the platform quality gate and the security zone's analysis token"):
        sonarqube.configure_gate()
        vault.bootstrap_client().write("kv/data/ci/security/sonarqube",
                                       {"data": {"url": sonarqube.INTERNAL_URL, "token": sonarqube.analysis_token()}})


def _start_workload() -> None:
    with console.step("start workload development data services (PostgreSQL, Redis, RabbitMQ, MinIO)"):
        compose.compose("up", "-d", "--wait", "--wait-timeout", "300", profiles=["workload"], timeout=600)


def _start_cluster() -> None:
    with console.step("create the kind cluster on the platform network and trust Harbor's CA"):
        cluster.create()
        cluster.trust_registry()
    with console.step("install External Secrets Operator, Kyverno, Argo CD and the Trivy Operator (pinned charts)"):
        cluster.install_addons()
    with console.step("give the cluster a pull-only registry robot"):
        registry.configure()
    with console.step("let External Secrets read Vault (JWT auth per namespace) and store workload secrets"):
        cluster.configure_vault()
    with console.step("apply the platform's cluster state and hand it to Argo CD"):
        cluster.install_platform_state()


def up(capabilities: list[str]) -> int:
    console.heading("Preparing the workstation")
    paths.ensure_local_dirs()
    with console.step("issue local CA and service certificates"):
        pki.ensure_all()
    with console.step("create platform networks"):
        docker.ensure_networks()
    with console.step("render Compose environment from versions.yaml and generated credentials"):
        compose.write_env_file()

    console.heading("Software factory")
    _start_core()
    selected = set(capabilities) | set(DEFAULT)
    if "registry" in selected:
        _start_registry()
    if "controlplane" in selected:
        _start_controlplane()
    if "quality" in selected:
        _start_quality()
    if "workload" in selected:
        _start_workload()
    if "ci" in selected:
        _start_ci()
    if "cluster" in selected:
        _start_cluster()
    console.ok("platform is up; run `sscp verify foundation` to check it")
    return 0


def down() -> int:
    console.heading("Stopping the software factory (data is kept)")
    if cluster.node_exists():
        with console.step("stop the kind cluster"):
            cluster.stop()
    if harbor.COMPOSE_FILE.exists():
        with console.step("stop Harbor"):
            harbor.compose("down", timeout=300)
    with console.step("stop platform services"):
        compose.write_env_file()
        compose.compose("down", profiles=ALL_PROFILES, timeout=300)
    return 0


def reset() -> int:
    """Removes every container, volume, network and generated credential of the platform."""
    console.heading("Resetting the platform: all platform data will be deleted")
    with console.step("delete the kind cluster"):
        cluster.delete()
    if harbor.COMPOSE_FILE.exists():
        with console.step("remove Harbor and its volumes"):
            harbor.compose("down", "-v", "--remove-orphans", timeout=300)
    if compose.ENV_FILE.exists():
        with console.step("remove platform services and their volumes"):
            compose.compose("down", "-v", "--remove-orphans", profiles=ALL_PROFILES, timeout=300)
    with console.step("remove platform networks"):
        docker.remove_networks()
    with console.step("delete generated certificates, credentials and configuration"):
        shutil.rmtree(paths.LOCAL, ignore_errors=True)
    console.ok("platform reset; `sscp up` starts from scratch")
    return 0


def status() -> int:
    console.heading("Platform containers")
    for project in (compose.PROJECT, harbor.PROJECT):
        result = docker.shell.run(
            ["docker", "ps", "-a", "--filter", f"label=com.docker.compose.project={project}",
             "--format", "{{.Label \"com.docker.compose.service\"}}\t{{.Status}}"],
            check=False,
        )
        for line in sorted(result.stdout.strip().splitlines()):
            service, _, state = line.partition("\t")
            healthy = "healthy" in state or ("Up" in state and "unhealthy" not in state and "starting" not in state)
            (console.ok if healthy else console.warn)(f"{project}/{service}: {state}")
    return 0
