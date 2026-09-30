"""Reads versions.yaml, the single source of truth for pinned versions."""
from __future__ import annotations

from functools import lru_cache
from typing import Any

import yaml

from sscp import paths


@lru_cache(maxsize=1)
def load() -> dict[str, Any]:
    with paths.VERSIONS_FILE.open(encoding="utf-8") as handle:
        data = yaml.safe_load(handle)
    if data.get("schemaVersion") != 1:
        raise ValueError(f"{paths.VERSIONS_FILE} has an unsupported schemaVersion")
    return data


def image(name: str) -> str:
    """Returns the pinned image reference for a logical image name, e.g. image('vault')."""
    images = load()["images"]
    if name not in images:
        raise KeyError(f"image '{name}' is not pinned in versions.yaml")
    return images[name]


def chart(name: str) -> dict[str, str]:
    return load()["charts"][name]


def tool(name: str) -> Any:
    return load()["tools"][name]
