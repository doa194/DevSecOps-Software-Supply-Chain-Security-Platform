"""Runs pinned tool images as sibling containers on the zone's private Docker daemon.

Inputs are copied into a job-private volume with `docker cp`, so a tool sees only what it
is given, whatever the runner's workspace layout. Source scanners run without any network:
they cannot fetch rules from the internet or send the code anywhere. Outputs are copied
back out and the volume is removed when the tool ends.
"""
from __future__ import annotations

import json
import os
import shlex
import subprocess
import uuid
from dataclasses import dataclass, field
from pathlib import Path

MANIFEST = Path(__file__).resolve().parent / "tool-images.json"


def images() -> dict[str, dict[str, str]]:
    return json.loads(MANIFEST.read_text(encoding="utf-8"))


def image(name: str) -> str:
    return images()[name]["image"]


def version(name: str) -> str:
    return images()[name]["version"]


@dataclass
class ToolRun:
    tool: str
    args: list[str]
    inputs: dict[str, Path] = field(default_factory=dict)   # name -> local dir, appears at /work/<name>
    network: str = "none"
    env: dict[str, str] = field(default_factory=dict)
    user: str | None = None
    # Named volumes that outlive one run (for example a scanner's database cache), which
    # stay inside the zone's private daemon.
    volumes: dict[str, str] = field(default_factory=dict)
    # Where the work volume appears in the tool container (some tools insist on a path).
    mount: str = "/work"
    # File or directory names deleted anywhere under /work/src before the tool starts.
    remove: tuple[str, ...] = ()


@dataclass
class ToolResult:
    exit_code: int
    stdout: str
    stderr: str
    outputs: Path


def _docker(*args: str, check: bool = True, capture: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["docker", *args], check=check, capture_output=capture, text=True)


def run(spec: ToolRun, output_dir: Path) -> ToolResult:
    suffix = f"{os.environ.get('GITHUB_RUN_ID', 'local')}-{uuid.uuid4().hex[:8]}"
    volume = f"sscp-work-{suffix}"
    loader = f"sscp-load-{suffix}"
    container = f"sscp-tool-{suffix}"
    _docker("volume", "create", volume)
    try:
        # A short-lived loader prepares /work: inputs readable by any user, /work/out writable
        # by the tool whatever user its image runs as.
        script = "mkdir -p /work/out && chmod 0777 /work /work/out && chmod -R a+rX /work"
        if spec.remove:
            names = " -o ".join(f"-name {shlex.quote(name)}" for name in spec.remove)
            script += f" && if [ -d /work/src ]; then find /work/src \\( {names} \\) -prune -exec rm -rf {{}} +; fi"
        _docker("create", "--name", loader, "--network", "none", "-v", f"{volume}:/work", image("alpine"), "sh", "-c", script)
        for name, local in spec.inputs.items():
            _docker("cp", f"{local}/.", f"{loader}:/work/{name}")
        _docker("start", "-a", loader)
        _docker("rm", loader)

        command = ["run", "--name", container, "--network", spec.network, "-v", f"{volume}:{spec.mount}", "-w", spec.mount,
                   "--security-opt", "no-new-privileges", "--cap-drop", "ALL"]
        if spec.user:
            command += ["--user", spec.user]
        for name, path in spec.volumes.items():
            command += ["-v", f"{name}:{path}"]
        for key, value in spec.env.items():
            command += ["-e", f"{key}={value}"]
        completed = _docker(*command, image(spec.tool), *spec.args, check=False)

        output_dir.mkdir(parents=True, exist_ok=True)
        _docker("cp", f"{container}:{spec.mount}/out/.", str(output_dir), check=False)
        return ToolResult(completed.returncode, completed.stdout, completed.stderr, output_dir)
    finally:
        _docker("rm", "-f", loader, check=False)
        _docker("rm", "-f", container, check=False)
        _docker("volume", "rm", "-f", volume, check=False)
