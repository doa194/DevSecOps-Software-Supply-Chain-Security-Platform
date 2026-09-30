"""Operational verification suites (`sscp verify <suite>`).

Each suite is a set of pytest modules marked `operational` that observe the running
platform. Keeping them as tests (rather than ad-hoc scripts) means the same checks run
from the command line, in documentation examples and during the final verification.
"""
from __future__ import annotations

import subprocess
import sys

from sscp import console, paths

TESTS = paths.PLATFORM_REPO / "automation" / "tests" / "operational"

SUITES: dict[str, list[str]] = {
    "foundation": ["test_foundation.py"],
    "controlplane": ["test_controlplane.py"],
    "ci-isolation": ["test_ci_isolation.py"],
    "registry": ["test_registry.py"],
    "signing": ["test_signing.py"],
    "cluster": ["test_cluster.py"],
    "observability": ["test_observability.py"],
    "recovery": ["test_recovery.py"],
}


def run(suites: list[str], extra: list[str] | None = None) -> int:
    selected = suites or list(SUITES)
    unknown = [suite for suite in selected if suite not in SUITES]
    if unknown:
        console.fail(f"unknown suite(s): {', '.join(unknown)}; available: {', '.join(SUITES)}")
        return 2
    files = [str(TESTS / name) for suite in selected for name in SUITES[suite]]
    console.heading(f"Operational verification: {', '.join(selected)}")
    command = [sys.executable, "-m", "pytest", "-m", "operational", "-p", "no:warnings", "-q", *files, *(extra or [])]
    return subprocess.run(command, cwd=paths.PLATFORM_REPO).returncode
