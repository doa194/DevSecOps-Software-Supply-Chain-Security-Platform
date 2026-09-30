"""Workstation prerequisite checks (`sscp doctor`).

Runs before any bootstrap so that a missing tool or an undersized Docker VM is reported
up front instead of surfacing as a confusing failure half-way through.
"""
from __future__ import annotations

import json
import shutil
import sys
from dataclasses import dataclass

from sscp import console, paths, pins, shell, versions

GIB = 1024**3


@dataclass
class CheckResult:
    name: str
    passed: bool
    detail: str
    required: bool = True


def _tool_version(tool: str, *args: str) -> str | None:
    if not shell.which(tool):
        return None
    result = shell.run([tool, *args], check=False, timeout=30)
    text = (result.stdout or result.stderr).strip().splitlines()
    return text[0] if text else "unknown version"


def check_docker() -> list[CheckResult]:
    if not shell.which("docker"):
        return [CheckResult("docker", False, "Docker CLI not found; install Docker Desktop")]
    result = shell.run(["docker", "info", "--format", "{{json .}}"], check=False, timeout=60)
    if result.returncode != 0:
        return [CheckResult("docker", False, "Docker daemon is not reachable; start Docker Desktop")]
    info = json.loads(result.stdout)
    memory = info.get("MemTotal", 0)
    cpus = info.get("NCPU", 0)
    return [
        CheckResult("docker", True, f"server {info.get('ServerVersion')} ({info.get('OperatingSystem')})"),
        # Measured with every capability started: about 10 GiB in use at rest, and a main
        # pipeline adds several GiB while it builds and tests. Below 16 GiB the Docker VM
        # swaps during pipelines, which slows everything down but still works.
        CheckResult(
            "docker memory",
            memory >= 16 * GIB,
            f"{memory / GIB:.1f} GiB available to Docker (16 GiB recommended; 12 GiB is the minimum "
            "for the full platform with the security-test environment)",
            required=memory >= 12 * GIB or memory == 0,
        ),
        CheckResult("docker cpus", cpus >= 4, f"{cpus} CPUs available to Docker (4 or more recommended)", required=False),
    ]


def check_disk() -> CheckResult:
    free = shutil.disk_usage(paths.WORKSPACE).free
    # Measured with every capability started: about 45 GB of images, volumes and build cache.
    return CheckResult(
        "disk space",
        free >= 50 * GIB,
        f"{free / GIB:.0f} GiB free on the workspace drive (50 GiB recommended; the full platform uses about 45 GB)",
        required=free >= 30 * GIB,
    )


def check_tools() -> list[CheckResult]:
    results = [
        CheckResult(
            "python",
            sys.version_info >= (3, 12),
            f"{sys.version.split()[0]} (3.12 or newer required)",
        )
    ]
    for tool, args, required, purpose in (
        ("git", ["--version"], True, "publishing repositories to Gitea"),
        ("kubectl", ["version", "--client"], True, "operating the kind cluster"),
        ("helm", ["version", "--short"], True, "installing Argo CD, Kyverno, External Secrets and the Trivy Operator"),
        ("dotnet", ["--version"], False, "building and testing the .NET code on the host (CI builds in containers)"),
        ("uv", ["--version"], False, "running the automation from a locked environment"),
    ):
        version = _tool_version(tool, *args)
        results.append(
            CheckResult(tool, version is not None, version or f"not found; needed for {purpose}", required=required)
        )
    return results


def check_pins() -> list[CheckResult]:
    data = versions.load()
    unpinned = pins.find_unpinned_images(data)
    unverified = pins.find_unverified_tools(data)
    return [
        CheckResult(
            "image pins",
            not unpinned,
            "every image in versions.yaml is pinned by digest" if not unpinned else "; ".join(map(str, unpinned)),
        ),
        CheckResult(
            "tool checksums",
            not unverified,
            "every downloaded tool has a SHA-256" if not unverified else "; ".join(unverified),
        ),
    ]


def run() -> int:
    console.heading("Workstation prerequisites")
    results = [*check_docker(), check_disk(), *check_tools(), *check_pins()]
    blocking = 0
    for result in results:
        if result.passed:
            console.ok(f"{result.name}: {result.detail}")
        elif result.required:
            console.fail(f"{result.name}: {result.detail}")
            blocking += 1
        else:
            console.warn(f"{result.name}: {result.detail}")
    if blocking:
        console.fail(f"{blocking} blocking problem(s); fix them before running the bootstrap")
        return 1
    console.ok("workstation is ready")
    return 0
