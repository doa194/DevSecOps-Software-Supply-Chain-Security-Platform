"""The job's zone identity: Vault AppRole login and short-lived tokens derived from it.

The runner gives every job its zone's AppRole role_id and secret_id. Vault only accepts
them from the runner's own address, so they are useless if copied elsewhere. From the
Vault token the job reads its zone's secrets and exchanges the zone's Keycloak client
secret for a Control Plane access token. All of these live only for the job.
"""
from __future__ import annotations

import os
import time
from functools import lru_cache

from sscp_ci import http


class ZoneError(RuntimeError):
    pass


def env(name: str) -> str:
    value = os.environ.get(name, "")
    if not value:
        raise ZoneError(f"{name} is not set; this command must run on a platform CI zone runner")
    return value


def zone() -> str:
    return env("SSCP_ZONE")


def run_id() -> int:
    # Gitea exposes the workflow run id under the GitHub-compatible name.
    return int(env("GITHUB_RUN_ID"))


@lru_cache(maxsize=1)
def vault_token() -> str:
    response = http.request("POST", f"{env('VAULT_ADDR')}/v1/auth/approle/login",
                            json_body={"role_id": env("SSCP_VAULT_ROLE_ID"), "secret_id": env("SSCP_VAULT_SECRET_ID")})
    return response.json()["auth"]["client_token"]


def secret(name: str) -> dict[str, str]:
    """Reads kv/ci/<zone>/<name> (KV version 2)."""
    response = http.request("GET", f"{env('VAULT_ADDR')}/v1/kv/data/ci/{zone()}/{name}", headers={"X-Vault-Token": vault_token()})
    return response.json()["data"]["data"]


@lru_cache(maxsize=1)
def _controlplane_client() -> dict[str, str]:
    return secret("controlplane")


_token: tuple[str, float] | None = None


def controlplane_token() -> str:
    """A Control Plane access token, renewed shortly before it expires: tokens live five
    minutes, and a single command (building every image, for example) can run longer."""
    global _token
    if _token is None or time.monotonic() >= _token[1]:
        client = _controlplane_client()
        response = http.request("POST", env("SSCP_KEYCLOAK_TOKEN_URL"), form={
            "grant_type": "client_credentials", "client_id": client["client_id"], "client_secret": client["client_secret"],
        })
        body = response.json()
        _token = (body["access_token"], time.monotonic() + int(body.get("expires_in", 300)) - 30)
    return _token[0]
