"""Harbor: generated deployment and administrative configuration.

Harbor's configuration is large and version-specific, so the platform uses Harbor's own
`prepare` generator instead of hand-written files. The generated Compose file assumes a
Linux host with /data paths and a syslog container; `adapt_compose` turns it into a
workstation-friendly deployment: named volumes, standard container logging, ports bound
to 127.0.0.1 and the proxy attached to the sscp-edge network as harbor.sscp.test.
"""
from __future__ import annotations

import hashlib
import json
import string
from pathlib import Path
from typing import Any

import yaml

from sscp import credentials, http, paths, shell, versions

PROJECT = "sscp-harbor"
BASE = paths.GENERATED_DIR / "harbor"
INPUT_DIR = BASE / "input"
DATA_DIR = BASE / "data"
COMPOSE_FILE = BASE / "docker-compose.yml"
FINGERPRINT_FILE = BASE / "inputs.sha256"
CONFIG_DIR = BASE / "common" / "config"
TEMPLATE = paths.PLATFORM_DIR / "harbor" / "harbor.yml.template"
PLACEHOLDER_DATA = "/harbor-data"

EDGE_ADDRESS = "172.30.0.11"
EXPORTER_ADDRESS = "172.30.0.16"

# Stateful directories Harbor keeps under its data volume, mapped to named volumes.
NAMED_VOLUMES = {
    f"{PLACEHOLDER_DATA}/registry": "harbor-registry",
    f"{PLACEHOLDER_DATA}/database": "harbor-database",
    f"{PLACEHOLDER_DATA}/redis": "harbor-redis",
    f"{PLACEHOLDER_DATA}/job_logs": "harbor-job-logs",
}


def admin_password() -> str:
    return credentials.get("harbor.admin")


def render_config() -> None:
    values = {
        "HARBOR_ADMIN_PASSWORD": admin_password(),
        "HARBOR_DB_PASSWORD": credentials.get("harbor.database"),
    }
    INPUT_DIR.mkdir(parents=True, exist_ok=True)
    rendered = string.Template(TEMPLATE.read_text(encoding="utf-8")).substitute(values)
    (INPUT_DIR / "harbor.yml").write_text(rendered, encoding="utf-8", newline="\n")


def run_generator() -> None:
    """Runs goharbor/prepare with explicit mounts instead of the host root filesystem."""
    for directory in (DATA_DIR, CONFIG_DIR):
        directory.mkdir(parents=True, exist_ok=True)
    mounts = {
        INPUT_DIR: "/input",
        DATA_DIR: "/data",
        BASE: "/compose_location",
        CONFIG_DIR: "/config",
        paths.PKI_DIR: "/hostfs/pki",
    }
    command = ["docker", "run", "--rm"]
    for source, target in mounts.items():
        command += ["--mount", f"type=bind,source={source.resolve().as_posix()},target={target}"]
    shell.run([*command, versions.image("harborPrepare"), "prepare"], timeout=300)


def _secret_bind(source: str, target: str) -> dict[str, Any]:
    """Maps a generated file under the data placeholder to its copy in DATA_DIR.

    Keys and certificates under secret/ are mounted read-only; other directories (for
    example the CA download folder) stay writable because Harbor manages their content.
    """
    relative = source.rstrip("/").removeprefix(PLACEHOLDER_DATA).lstrip("/")
    local = DATA_DIR / relative if relative else DATA_DIR
    read_only = relative.startswith("secret")
    return {"type": "bind", "source": local.resolve().as_posix(), "target": target, "read_only": read_only}


def _adapt_volume(volume: Any, named: set[str]) -> Any:
    if isinstance(volume, str):
        source, target, *_ = volume.split(":")
        source = source.rstrip("/")
        if source in NAMED_VOLUMES:
            named.add(NAMED_VOLUMES[source])
            return {"type": "volume", "source": NAMED_VOLUMES[source], "target": target}
        if source.startswith(PLACEHOLDER_DATA):
            return _secret_bind(source, target)
        if source.startswith("./"):
            return {"type": "bind", "source": (BASE / source[2:]).resolve().as_posix(), "target": target}
        return volume
    if isinstance(volume, dict) and str(volume.get("source", "")).startswith(PLACEHOLDER_DATA):
        return _secret_bind(volume["source"], volume["target"])
    if isinstance(volume, dict) and str(volume.get("source", "")).startswith("./"):
        return {**volume, "source": (BASE / volume["source"][2:]).resolve().as_posix()}
    return volume


def adapt_compose(document: dict[str, Any]) -> dict[str, Any]:
    """Turns the generator's Linux-host Compose file into the workstation deployment."""
    services: dict[str, Any] = document["services"]
    services.pop("log", None)  # the syslog collector is replaced by normal container logs
    named: set[str] = set()

    for name, service in services.items():
        service.pop("container_name", None)
        service.pop("logging", None)
        depends = service.get("depends_on")
        if isinstance(depends, list):
            service["depends_on"] = [d for d in depends if d != "log"]
        elif isinstance(depends, dict):
            depends.pop("log", None)
        service["volumes"] = [_adapt_volume(v, named) for v in service.get("volumes", [])]
        service["restart"] = "unless-stopped"

    proxy = services["proxy"]
    proxy["ports"] = [f"127.0.0.1:8443:8443"]
    proxy["networks"] = {
        "harbor": {},
        "edge": {"ipv4_address": EDGE_ADDRESS, "aliases": ["harbor.sscp.test"]},
    }
    if "exporter" in services:
        services["exporter"]["networks"] = {
            "harbor": {},
            "edge": {"ipv4_address": EXPORTER_ADDRESS, "aliases": ["harbor-exporter.sscp.test"]},
        }

    networks = document.setdefault("networks", {})
    # Harbor's components talk to each other on this network. Making it internal means a
    # CI job on sscp-edge can reach Harbor only through the TLS proxy, never its database,
    # Redis or registry backend directly.
    networks["harbor"] = {**(networks.get("harbor") or {}), "internal": True}
    networks["edge"] = {"name": "sscp-edge", "external": True}
    document["volumes"] = {volume: {} for volume in sorted(named)}
    return document


def _inputs_fingerprint() -> str:
    digest = hashlib.sha256()
    digest.update((INPUT_DIR / "harbor.yml").read_bytes())
    digest.update(versions.image("harborPrepare").encode())
    digest.update(Path(__file__).read_bytes())  # the post-processing is an input too
    return digest.hexdigest()


def generate() -> Path:
    """Generates the deployment, but only when its inputs changed.

    Harbor's generator creates fresh internal secrets on every run. Re-running it without
    need would recreate Harbor's containers on every `sscp up`, so an unchanged input
    fingerprint keeps the previous output.
    """
    render_config()
    fingerprint = _inputs_fingerprint()
    if COMPOSE_FILE.exists() and FINGERPRINT_FILE.exists() and FINGERPRINT_FILE.read_text(encoding="utf-8").strip() == fingerprint:
        return COMPOSE_FILE
    run_generator()
    document = yaml.safe_load(COMPOSE_FILE.read_text(encoding="utf-8"))
    COMPOSE_FILE.write_text(yaml.safe_dump(adapt_compose(document), sort_keys=False), encoding="utf-8", newline="\n")
    FINGERPRINT_FILE.write_text(fingerprint, encoding="utf-8")
    return COMPOSE_FILE


# Harbor's registry and job service run as uid 10000, but Docker creates named volumes
# owned by root. Harbor's own installer fixes this on the host; here it is done once in a
# throw-away container.
HARBOR_UID = "10000:10000"
OWNED_BY_HARBOR = ["harbor-registry", "harbor-job-logs"]


def fix_volume_ownership() -> None:
    for volume in OWNED_BY_HARBOR:
        shell.run(["docker", "run", "--rm", "--network", "none", "-v", f"{PROJECT}_{volume}:/target", versions.image("alpine"),
                   "chown", "-R", HARBOR_UID, "/target"], timeout=300)


def refresh_proxy_if_stale() -> None:
    """Restarts Harbor's nginx proxy when a component behind it was (re)started later.

    nginx resolves its upstream names once at start-up. A recreated core, portal or
    registry gets a new address, and the proxy would keep sending traffic to the old one.
    """
    started = {}
    for line in compose("ps", "--format", "json").stdout.splitlines():
        if line.strip():
            container = json.loads(line)
            started[container["Service"]] = shell.output(["docker", "inspect", "--format", "{{.State.StartedAt}}", container["Name"]])
    proxy_started = started.pop("proxy", None)
    if proxy_started is not None and any(value > proxy_started for value in started.values()):
        compose("restart", "proxy", timeout=120)


def compose(*args: str, check: bool = True, timeout: float | None = None):
    return shell.run(["docker", "compose", "-p", PROJECT, "-f", str(COMPOSE_FILE), *args], check=check, timeout=timeout)


class HarborApi:
    """Harbor REST API as the bootstrap administrator."""

    def __init__(self) -> None:
        self._client = http.client("harbor", auth=("admin", admin_password()))

    def request(self, method: str, path: str, expected: tuple[int, ...] = (), **kwargs):
        # Basic auth on every call and no session cookie: with a cookie Harbor would demand
        # a CSRF token, as it does for browser sessions.
        self._client.cookies.clear()
        response = self._client.request(method, f"/api/v2.0/{path.lstrip('/')}", **kwargs)
        if response.status_code >= 400 and response.status_code not in expected:
            raise RuntimeError(f"Harbor {method} {path} failed: {response.status_code} {response.text[:300]}")
        return response

    def ensure_project(self, name: str, public: bool) -> None:
        if self.request("HEAD", "projects", params={"project_name": name}, expected=(404,)).status_code == 404:
            self.request("POST", "projects", json={"project_name": name, "metadata": {"public": str(public).lower()}, "storage_limit": -1})
        else:
            project = self.request("GET", f"projects/{name}").json()
            self.request("PUT", f"projects/{project['project_id']}", json={"metadata": {"public": str(public).lower()}})
