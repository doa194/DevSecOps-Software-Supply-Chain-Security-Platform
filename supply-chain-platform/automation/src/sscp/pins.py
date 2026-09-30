"""Pinning policy for supply-chain dependencies of the platform itself.

Every container image the platform runs must be referenced by an immutable digest.
A tag alone can be moved by whoever controls the upstream repository, so an unpinned
image would let an upstream change alter the platform without review.
"""
from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any

DIGEST = re.compile(r"@sha256:[0-9a-f]{64}$")

# Entries in the images section that are version strings rather than image references.
NON_IMAGE_KEYS = {"harborVersion"}


@dataclass(frozen=True)
class PinViolation:
    name: str
    reference: str

    def __str__(self) -> str:
        return f"{self.name}: '{self.reference}' is not pinned by digest"


def find_unpinned_images(versions: dict[str, Any]) -> list[PinViolation]:
    images = dict(versions.get("images", {}))
    images["kubernetes.nodeImage"] = versions.get("kubernetes", {}).get("nodeImage", "")
    return [
        PinViolation(name, str(reference))
        for name, reference in images.items()
        if name not in NON_IMAGE_KEYS and not DIGEST.search(str(reference))
    ]


def find_unverified_tools(versions: dict[str, Any]) -> list[str]:
    """Downloaded binaries must carry a SHA-256 so a tampered download is rejected."""
    problems = []
    for name, spec in versions.get("tools", {}).items():
        if isinstance(spec, dict) and "url" in spec and not re.fullmatch(r"[0-9a-f]{64}", str(spec.get("sha256", ""))):
            problems.append(f"{name}: download has no SHA-256 checksum")
    return problems
