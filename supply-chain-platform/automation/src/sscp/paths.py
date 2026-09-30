"""Well-known locations in the workspace.

The workspace holds the sources of all three Gitea repositories side by side:

    <workspace>/supply-chain-platform   (this repository, where the automation lives)
    <workspace>/commerce-app
    <workspace>/commerce-gitops

Generated, workstation-specific material (certificates, credentials, tool binaries,
run records) lives under <workspace>/.local and <workspace>/.tools and is never committed.
"""
from __future__ import annotations

from pathlib import Path

PLATFORM_REPO = Path(__file__).resolve().parents[3]
WORKSPACE = PLATFORM_REPO.parent
COMMERCE_APP_REPO = WORKSPACE / "commerce-app"
GITOPS_REPO = WORKSPACE / "commerce-gitops"

VERSIONS_FILE = PLATFORM_REPO / "versions.yaml"
PLATFORM_DIR = PLATFORM_REPO / "platform"
CLUSTER_DIR = PLATFORM_REPO / "cluster"
CI_DIR = PLATFORM_REPO / "ci"

LOCAL = WORKSPACE / ".local"
PKI_DIR = LOCAL / "pki"
SECRETS_DIR = LOCAL / "secrets"
GENERATED_DIR = LOCAL / "generated"
RUNS_DIR = WORKSPACE / ".runs"
TOOLS_BIN = WORKSPACE / ".tools" / "bin"


def ensure_local_dirs() -> None:
    for directory in (PKI_DIR, SECRETS_DIR, GENERATED_DIR, RUNS_DIR, TOOLS_BIN):
        directory.mkdir(parents=True, exist_ok=True)
