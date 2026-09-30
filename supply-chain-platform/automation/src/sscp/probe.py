"""Runs short Python probes inside a container attached to a platform network.

Operational tests use this to observe the platform from the point of view of a CI job or
cluster node (inside sscp-edge) rather than from the host, because network boundaries
only exist inside Docker.
"""
from __future__ import annotations

import json
import subprocess
import textwrap
import uuid

from sscp import cizones, compose, docker, pki, shell, versions


def run_in_zone(zone_name: str, script: str) -> dict:
    """Executes `script` in a throw-away ci-tools container on a zone runner's private
    daemon, so its traffic leaves from that runner's address like a real job's."""
    runner = docker.compose_container(compose.PROJECT, cizones.zone(zone_name).service)
    config = (cizones.RUNNERS_DIR / zone_name / "config.yaml").read_text(encoding="utf-8")
    ci_tools = next(line.split("docker://")[1] for line in config.splitlines() if "docker://" in line).strip().strip('"')
    result = docker.exec_in(runner, ["docker", "run", "--rm", ci_tools, "python3", "-c", textwrap.dedent(script)])
    return json.loads(result.stdout.strip().splitlines()[-1])


def run_python(network: str, script: str, timeout: float = 120) -> dict:
    """Executes `script` with the local CA at /ca.crt; the script must print one JSON object."""
    name = f"sscp-probe-{uuid.uuid4().hex[:8]}"
    command = [
        "docker", "run", "--rm", "--name", name, "--network", network,
        "--mount", f"type=bind,source={pki.CA_CERT.resolve().as_posix()},target=/ca.crt,readonly",
        versions.image("python"), "python", "-c", textwrap.dedent(script),
    ]
    try:
        output = shell.output(command, timeout=timeout)
    except subprocess.TimeoutExpired:
        # Stopping the docker client does not stop the container; remove it explicitly.
        shell.run(["docker", "rm", "-f", name], check=False, timeout=60)
        raise
    return json.loads(output.splitlines()[-1])


TLS_CHECK = """
import json, socket, ssl
results = {}
context = ssl.create_default_context(cafile="/ca.crt")
for host, port in TARGETS:
    try:
        with socket.create_connection((host, port), timeout=5) as raw:
            with context.wrap_socket(raw, server_hostname=host) as tls:
                results[host] = "verified:" + tls.version()
    except Exception as error:
        results[host] = "error:" + type(error).__name__ + ":" + str(error)[:120]
print(json.dumps(results))
"""

# Reports name resolution and TCP reachability separately: hiding a name is not a
# boundary if the address can still be reached directly.
TCP_CHECK = """
import json, socket
results = {}
for host, port in TARGETS:
    entry = {"resolved": False, "reachable": False}
    try:
        address = socket.gethostbyname(host)
        entry["resolved"] = True
    except OSError:
        address = IPS.get(host)
    if address:
        try:
            socket.create_connection((address, port), timeout=3).close()
            entry["reachable"] = True
        except OSError:
            pass
    results[host] = entry
print(json.dumps(results))
"""


def tls_check(network: str, targets: list[tuple[str, int]]) -> dict:
    return run_python(network, f"TARGETS = {json.dumps(targets)}\n" + TLS_CHECK)


def tcp_check(network: str, targets: list[tuple[str, int]], ips: dict[str, str]) -> dict:
    return run_python(network, f"TARGETS = {json.dumps(targets)}\nIPS = {json.dumps(ips)}\n" + TCP_CHECK)
