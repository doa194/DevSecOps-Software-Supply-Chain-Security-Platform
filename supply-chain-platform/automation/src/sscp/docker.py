"""Docker helpers: platform networks, container state and waiting for health."""
from __future__ import annotations

import json
import time
from dataclasses import dataclass

from sscp import shell


@dataclass(frozen=True)
class Network:
    name: str
    subnet: str
    ip_range: str | None = None
    gateway: str | None = None
    # Docker does not stop routed traffic between ordinary bridge networks on this engine,
    # so separate networks alone are not a boundary. An internal network drops all traffic
    # that does not stay inside it, which is what keeps CI jobs away from the databases.
    internal: bool = False


# Fixed subnets: runners and services have static addresses that Vault policies and
# Kubernetes NetworkPolicies refer to, so the address plan must never change silently.
EDGE = Network("sscp-edge", "172.30.0.0/16", ip_range="172.30.128.0/17", gateway="172.30.0.1")
DATA = Network("sscp-data", "172.31.0.0/24", ip_range="172.31.0.128/25", gateway="172.31.0.1", internal=True)
WORKLOAD_DEV = Network("sscp-workload-dev", "172.32.0.0/24", ip_range="172.32.0.128/25", gateway="172.32.0.1")
NETWORKS = (EDGE, DATA, WORKLOAD_DEV)


def network_exists(name: str) -> bool:
    return shell.run(["docker", "network", "inspect", name], check=False).returncode == 0


def ensure_networks() -> None:
    for network in NETWORKS:
        if network_exists(network.name):
            internal = shell.output(["docker", "network", "inspect", "--format", "{{.Internal}}", network.name]) == "true"
            if internal != network.internal:
                raise RuntimeError(
                    f"network {network.name} exists with internal={internal}, expected {network.internal}; "
                    "run `sscp down` and `docker network rm` it, then `sscp up` again"
                )
            continue
        command = ["docker", "network", "create", "--driver", "bridge", "--subnet", network.subnet]
        if network.ip_range:
            command += ["--ip-range", network.ip_range]
        if network.gateway:
            command += ["--gateway", network.gateway]
        if network.internal:
            command.append("--internal")
        shell.run([*command, network.name])


def remove_networks() -> None:
    for network in NETWORKS:
        if network_exists(network.name):
            shell.run(["docker", "network", "rm", network.name], check=False)


def container_state(name: str) -> dict | None:
    result = shell.run(["docker", "inspect", "--format", "{{json .State}}", name], check=False)
    if result.returncode != 0:
        return None
    return json.loads(result.stdout)


def compose_container(project: str, service: str) -> str | None:
    """Returns the container name of a Compose service, or None if it is not running."""
    result = shell.run(
        ["docker", "ps", "-q", "--filter", f"label=com.docker.compose.project={project}",
         "--filter", f"label=com.docker.compose.service={service}"],
        check=False,
    )
    container_id = result.stdout.strip().splitlines()
    if not container_id:
        return None
    return shell.output(["docker", "inspect", "--format", "{{.Name}}", container_id[0]]).lstrip("/")


def wait_healthy(container: str, timeout: float = 300) -> None:
    deadline = time.monotonic() + timeout
    last = "unknown"
    while time.monotonic() < deadline:
        state = container_state(container)
        if state is None:
            last = "missing"
        elif state.get("Health"):
            last = state["Health"]["Status"]
            if last == "healthy":
                return
        elif state.get("Running"):
            return  # no healthcheck defined; running is the best signal available
        else:
            last = state.get("Status", "stopped")
        time.sleep(3)
    raise TimeoutError(f"{container} did not become healthy within {timeout:.0f}s (last state: {last})")


def exec_in(container: str, command: list[str], *, user: str | None = None, check: bool = True,
            input_text: str | None = None, env: dict[str, str] | None = None):
    args = ["docker", "exec"]
    if input_text is not None:
        args.append("-i")
    if user:
        args += ["--user", user]
    for key, value in (env or {}).items():
        args += ["-e", f"{key}={value}"]
    return shell.run([*args, container, *command], check=check, input_text=input_text)
