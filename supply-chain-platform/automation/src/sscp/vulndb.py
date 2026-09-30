"""Vulnerability databases for the offline scanners (`sscp up --with ci`, `sscp tools refresh-db`).

Trivy and Grype need current vulnerability data. The platform copies it into Harbor's
platform-tools project, so scans in the security zone run without internet access. The
data is as current as the last refresh; the trust policy refuses a scan whose database is
older than seven days, so a forgotten refresh fails closed instead of passing silently.
"""
from __future__ import annotations

import hashlib
from pathlib import Path

import httpx

from sscp import paths, toolmirror

TRIVY_SOURCE = "ghcr.io/aquasecurity/trivy-db:2"
TRIVY_TARGET = f"{toolmirror.REGISTRY}/{toolmirror.PROJECT}/trivy-db:2"
# Trivy's misconfiguration checks, used by the Trivy Operator's configuration audits of the
# running cluster.
CHECKS_SOURCE = "ghcr.io/aquasecurity/trivy-checks:1"
CHECKS_TARGET = f"{toolmirror.REGISTRY}/{toolmirror.PROJECT}/trivy-checks:1"
GRYPE_LISTING = "https://grype.anchore.io/databases/v6/latest.json"
GRYPE_TARGET = f"{toolmirror.REGISTRY}/{toolmirror.PROJECT}/grype-db:v6"
GRYPE_DIR = paths.GENERATED_DIR / "grype-db"
GRYPE_MEDIA_TYPE = "application/vnd.sscp.grype-db.v6.tar+zstd"


def refresh_trivy() -> str:
    """Copies the current Trivy database artifact; returns its digest in Harbor."""
    upstream = toolmirror.registry_tool("crane", ["digest", TRIVY_SOURCE], timeout=120)
    if toolmirror.registry_tool("crane", ["digest", TRIVY_TARGET], check=False, timeout=60) != upstream:
        toolmirror.registry_tool("crane", ["copy", TRIVY_SOURCE, TRIVY_TARGET], timeout=1800)
    return upstream


def refresh_trivy_checks() -> str:
    """Copies the current Trivy checks bundle; returns its digest in Harbor."""
    upstream = toolmirror.registry_tool("crane", ["digest", CHECKS_SOURCE], timeout=120)
    if toolmirror.registry_tool("crane", ["digest", CHECKS_TARGET], check=False, timeout=60) != upstream:
        toolmirror.registry_tool("crane", ["copy", CHECKS_SOURCE, CHECKS_TARGET], timeout=600)
    return upstream


def refresh_grype() -> str:
    """Downloads the current Grype database (checksum-verified) and publishes it with ORAS."""
    listing = httpx.get(GRYPE_LISTING, timeout=60).raise_for_status().json()
    # The published name contains colons (a timestamp), which Windows file names cannot.
    archive = GRYPE_DIR / Path(listing["path"]).name.replace(":", "")
    expected = listing["checksum"].removeprefix("sha256:")
    GRYPE_DIR.mkdir(parents=True, exist_ok=True)
    if not archive.exists() or _sha256(archive) != expected:
        url = GRYPE_LISTING.rsplit("/", 1)[0] + "/" + listing["path"]
        with httpx.stream("GET", url, timeout=600, follow_redirects=True) as response, archive.open("wb") as file:
            response.raise_for_status()
            for chunk in response.iter_bytes(1 << 20):
                file.write(chunk)
        if _sha256(archive) != expected:
            archive.unlink()
            raise RuntimeError(f"the Grype database download does not match its published checksum ({listing['path']})")
    for stale in GRYPE_DIR.glob("*.tar.zst"):
        if stale != archive:
            stale.unlink()

    toolmirror.registry_tool("oras", [
        "push", "--registry-config", "/docker/config.json", "--artifact-type", "application/vnd.sscp.grype-db",
        "--annotation", f"org.opencontainers.image.created={listing['built']}",
        "--annotation", f"io.anchore.grype.schema={listing['schemaVersion']}",
        GRYPE_TARGET, f"{archive.name}:{GRYPE_MEDIA_TYPE}",
    ], mounts=[f"type=bind,source={GRYPE_DIR.resolve().as_posix()},target=/workspace,readonly"], timeout=1800)
    return listing["built"]


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for chunk in iter(lambda: file.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def refresh() -> dict[str, str]:
    return {"trivy": refresh_trivy(), "trivy-checks": refresh_trivy_checks(), "grype": refresh_grype()}
