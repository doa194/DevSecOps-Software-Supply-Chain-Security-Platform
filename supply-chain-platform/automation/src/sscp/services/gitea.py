"""Gitea: administrator account and API access.

The administrator account is a bootstrap identity: it creates organisations, teams,
users, repositories and protections. Day-to-day identities (developers, reviewers, the
release manager and platform bots) are separate accounts with narrower permissions.
"""
from __future__ import annotations

from typing import Any

import httpx

from sscp import compose, credentials, docker, http

ADMIN_USER = "sscp-admin"


def admin_password() -> str:
    return credentials.get("gitea.admin")


def ensure_admin() -> None:
    container = docker.compose_container(compose.PROJECT, "gitea")
    if container is None:
        raise RuntimeError("Gitea is not running")
    listed = docker.exec_in(container, ["gitea", "admin", "user", "list", "--admin"], check=False)
    if ADMIN_USER in listed.stdout:
        return
    docker.exec_in(container, [
        "gitea", "admin", "user", "create", "--admin",
        "--username", ADMIN_USER,
        "--password", admin_password(),
        "--email", f"{ADMIN_USER}@sscp.test",
        "--must-change-password=false",
    ])


class Gitea:
    """Thin REST client; `sudo` performs a call as another user (admin impersonation)."""

    def __init__(self, username: str = ADMIN_USER, password: str | None = None, token: str | None = None):
        auth = None if token else (username, password or admin_password())
        headers = {"Authorization": f"token {token}"} if token else {}
        self._client = http.client("gitea", auth=auth, headers=headers)

    def request(self, method: str, path: str, *, sudo: str | None = None, expected: tuple[int, ...] = (), **kwargs) -> httpx.Response:
        headers = kwargs.pop("headers", {})
        if sudo:
            headers["Sudo"] = sudo
        response = self._client.request(method, f"/api/v1/{path.lstrip('/')}", headers=headers, **kwargs)
        if response.status_code >= 400 and response.status_code not in expected:
            raise RuntimeError(f"Gitea {method} {path} failed: {response.status_code} {response.text[:300]}")
        return response

    def get(self, path: str, **kwargs) -> Any:
        response = self.request("GET", path, expected=(404,), **kwargs)
        return None if response.status_code == 404 else response.json()

    def post(self, path: str, payload: dict | list | None = None, **kwargs) -> Any:
        response = self.request("POST", path, json=payload, **kwargs)
        return response.json() if response.content else None

    def patch(self, path: str, payload: dict, **kwargs) -> Any:
        response = self.request("PATCH", path, json=payload, **kwargs)
        return response.json() if response.content else None

    def put(self, path: str, payload: dict | None = None, **kwargs) -> Any:
        response = self.request("PUT", path, json=payload, **kwargs)
        return response.json() if response.content else None

    def delete(self, path: str, **kwargs) -> None:
        self.request("DELETE", path, expected=(404,), **kwargs)


def wait_ready() -> None:
    http.wait_for("Gitea API", lambda: http.client("gitea").get("/api/healthz").status_code == 200)
