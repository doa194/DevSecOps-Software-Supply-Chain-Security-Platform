"""Keycloak: applies realm definitions from platform/keycloak/realms.

A realm file describes roles, clients and personas; this module makes Keycloak match it
through the Admin REST API. Re-running is safe: existing objects are left as they are and
missing ones are created. Secrets are generated here and stored in the credential store,
never in the realm file.
"""
from __future__ import annotations

from typing import Any

import httpx
import yaml

from sscp import credentials, http, paths

REALMS_DIR = paths.PLATFORM_DIR / "keycloak" / "realms"


def admin_password() -> str:
    return credentials.get("keycloak.bootstrap-admin")


def wait_ready() -> None:
    http.wait_for(
        "Keycloak",
        lambda: http.client("keycloak").get("/realms/master/.well-known/openid-configuration").status_code == 200,
        timeout=300,
    )


class KeycloakAdmin:
    def __init__(self) -> None:
        self._client = http.client("keycloak")
        response = self._client.post(
            "/realms/master/protocol/openid-connect/token",
            data={"grant_type": "password", "client_id": "admin-cli", "username": "bootstrap-admin", "password": admin_password()},
        )
        response.raise_for_status()
        self._client.headers["Authorization"] = f"Bearer {response.json()['access_token']}"

    def get(self, path: str, **params) -> Any:
        response = self._client.get(f"/admin/realms/{path}", params=params)
        if response.status_code == 404:
            return None
        response.raise_for_status()
        return response.json() if response.content else None

    def post(self, path: str, payload: Any, allow_conflict: bool = True) -> httpx.Response:
        response = self._client.post(f"/admin/realms/{path}", json=payload)
        if response.status_code == 409 and allow_conflict:
            return response
        if response.status_code >= 400:
            raise RuntimeError(f"Keycloak POST {path} failed: {response.status_code} {response.text[:300]}")
        return response

    def put(self, path: str, payload: Any) -> None:
        response = self._client.put(f"/admin/realms/{path}", json=payload)
        if response.status_code >= 400:
            raise RuntimeError(f"Keycloak PUT {path} failed: {response.status_code} {response.text[:300]}")

    def create_realm(self, name: str, settings: dict[str, Any]) -> None:
        if self.get(name) is None:
            response = self._client.post("/admin/realms", json={"realm": name, "enabled": True, **settings})
            response.raise_for_status()
        else:
            self.put(name, {"realm": name, "enabled": True, **settings})

    def client_by_id(self, realm: str, client_id: str) -> dict | None:
        found = self.get(f"{realm}/clients", clientId=client_id) or []
        return found[0] if found else None

    def user_by_name(self, realm: str, username: str) -> dict | None:
        found = self.get(f"{realm}/users", username=username, exact="true") or []
        return found[0] if found else None


def client_secret(realm: str, client_id: str) -> str:
    return credentials.get(f"keycloak.{realm}.client.{client_id}")


def persona_password(realm: str, username: str) -> str:
    return credentials.get(f"keycloak.{realm}.user.{username}", lambda: credentials.password(20))


def _ensure_client(admin: KeycloakAdmin, realm: str, client_id: str, spec: dict[str, Any]) -> dict:
    public = spec.get("public", False)
    representation = {
        "clientId": client_id,
        "enabled": True,
        "protocol": "openid-connect",
        "publicClient": public,
        "bearerOnly": spec.get("bearerOnly", False),
        "standardFlowEnabled": spec.get("standardFlow", False),
        "directAccessGrantsEnabled": spec.get("directAccessGrants", False),
        "serviceAccountsEnabled": spec.get("serviceAccount", False),
        "implicitFlowEnabled": False,
        "fullScopeAllowed": True,
    }
    if lifespan := spec.get("accessTokenLifespan"):
        representation["attributes"] = {"access.token.lifespan": str(lifespan)}
    if not public and not spec.get("bearerOnly", False):
        representation["secret"] = client_secret(realm, client_id)
    existing = admin.client_by_id(realm, client_id)
    if existing is None:
        admin.post(f"{realm}/clients", representation)
        existing = admin.client_by_id(realm, client_id)
    else:
        admin.put(f"{realm}/clients/{existing['id']}", {**existing, **representation})

    if audience := spec.get("audience"):
        mappers = admin.get(f"{realm}/clients/{existing['id']}/protocol-mappers/models") or []
        if not any(m["name"] == f"{audience}-audience" for m in mappers):
            admin.post(f"{realm}/clients/{existing['id']}/protocol-mappers/models", {
                "name": f"{audience}-audience",
                "protocol": "openid-connect",
                "protocolMapper": "oidc-audience-mapper",
                "config": {"included.client.audience": audience, "access.token.claim": "true", "id.token.claim": "false"},
            })

    if roles := spec.get("realmManagementRoles"):
        service_user = admin.get(f"{realm}/clients/{existing['id']}/service-account-user")
        management = admin.client_by_id(realm, "realm-management")
        wanted = [admin.get(f"{realm}/clients/{management['id']}/roles/{role}") for role in roles]
        admin.post(f"{realm}/users/{service_user['id']}/role-mappings/clients/{management['id']}", wanted)

    if spec.get("serviceAccount") and (realm_roles := spec.get("serviceAccountRealmRoles")):
        service_user = admin.get(f"{realm}/clients/{existing['id']}/service-account-user")
        admin.post(f"{realm}/users/{service_user['id']}/role-mappings/realm", [admin.get(f"{realm}/roles/{r}") for r in realm_roles])
    return existing


def _ensure_persona(admin: KeycloakAdmin, realm: str, username: str, spec: dict[str, Any]) -> None:
    user = admin.user_by_name(realm, username)
    if user is None:
        admin.post(f"{realm}/users", {
            "username": username,
            "email": f"{username}@{realm}.test",
            "emailVerified": True,
            "enabled": True,
            "firstName": spec.get("firstName", username),
            "lastName": spec.get("lastName", "User"),
            "credentials": [{"type": "password", "value": persona_password(realm, username), "temporary": False}],
        }, allow_conflict=False)
        user = admin.user_by_name(realm, username)
    roles = [admin.get(f"{realm}/roles/{role}") for role in spec.get("roles", [])]
    if roles:
        admin.post(f"{realm}/users/{user['id']}/role-mappings/realm", roles)


def apply_realm(name: str) -> None:
    definition = yaml.safe_load((REALMS_DIR / f"{name}.yaml").read_text(encoding="utf-8"))
    admin = KeycloakAdmin()
    realm = definition["realm"]
    admin.create_realm(realm, {"displayName": definition.get("displayName", realm), **definition.get("settings", {})})
    for role, description in definition.get("roles", {}).items():
        admin.post(f"{realm}/roles", {"name": role, "description": description})
    for client_id, spec in definition.get("clients", {}).items():
        _ensure_client(admin, realm, client_id, spec or {})
    for username, spec in definition.get("personas", {}).items():
        _ensure_persona(admin, realm, username, spec or {})


def user_token(realm: str, username: str, client_id: str = "commerce-cli") -> str:
    """Obtains an access token for a persona (password grant through the public test client)."""
    with http.client("keycloak") as client:
        response = client.post(f"/realms/{realm}/protocol/openid-connect/token", data={
            "grant_type": "password", "client_id": client_id, "username": username,
            "password": persona_password(realm, username), "scope": "openid",
        })
        response.raise_for_status()
        return response.json()["access_token"]
