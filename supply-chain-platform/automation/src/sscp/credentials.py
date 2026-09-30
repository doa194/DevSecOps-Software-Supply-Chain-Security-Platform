"""Generated bootstrap credentials for the local platform.

Every password, token and key the bootstrap needs is generated randomly on first use and
stored under .local/secrets (git-ignored). Nothing secret is ever written into the
repositories. Steady-state credentials for CI zones and workloads are moved into Vault
by later bootstrap steps; this store keeps only bootstrap and administrator material.
"""
from __future__ import annotations

import json
import os
import secrets as random  # standard library CSPRNG
import string
from pathlib import Path
from typing import Any, Callable

from sscp import paths

STORE = paths.SECRETS_DIR / "bootstrap.json"

_ALPHABET = string.ascii_letters + string.digits


def password(length: int = 32) -> str:
    """Random password using only letters and digits, safe in URLs, env files and YAML."""
    return "".join(random.choice(_ALPHABET) for _ in range(length))


def hex_token(num_bytes: int = 32) -> str:
    return random.token_hex(num_bytes)


def _load() -> dict[str, Any]:
    if STORE.exists():
        return json.loads(STORE.read_text(encoding="utf-8"))
    return {}


def _save(data: dict[str, Any]) -> None:
    paths.SECRETS_DIR.mkdir(parents=True, exist_ok=True)
    temporary = STORE.with_suffix(".tmp")
    temporary.write_text(json.dumps(data, indent=2, sort_keys=True), encoding="utf-8")
    os.replace(temporary, STORE)


def get(name: str, generator: Callable[[], Any] = password) -> Any:
    """Returns a stored value, generating and persisting it on first use."""
    data = _load()
    if name not in data:
        data[name] = generator()
        _save(data)
    return data[name]


def put(name: str, value: Any) -> None:
    data = _load()
    data[name] = value
    _save(data)


def find(name: str) -> Any | None:
    return _load().get(name)


def write_file(relative: str, content: str | bytes) -> Path:
    """Writes a credential file (for example an AppRole secret) under .local/secrets."""
    target = paths.SECRETS_DIR / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(content, bytes):
        target.write_bytes(content)
    else:
        target.write_text(content, encoding="utf-8", newline="\n")
    return target
