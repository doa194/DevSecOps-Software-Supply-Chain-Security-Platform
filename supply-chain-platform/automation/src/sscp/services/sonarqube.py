"""SonarQube: replaces the well-known default administrator password on first start."""
from __future__ import annotations

from sscp import credentials, http

DEFAULT_PASSWORD = "admin"


def admin_password() -> str:
    # SonarQube requires at least 12 characters with mixed classes.
    return credentials.get("sonarqube.admin", lambda: credentials.password(24) + "!9a")


def wait_ready() -> None:
    http.wait_for(
        "SonarQube",
        lambda: http.client("sonarqube").get("/api/system/status").json().get("status") == "UP",
        timeout=600,
        interval=5,
    )


def rotate_default_password() -> None:
    with http.client("sonarqube") as client:
        if client.get("/api/authentication/validate", auth=("admin", admin_password())).json().get("valid"):
            return
        response = client.post(
            "/api/users/change_password",
            auth=("admin", DEFAULT_PASSWORD),
            data={"login": "admin", "previousPassword": DEFAULT_PASSWORD, "password": admin_password()},
        )
        if response.status_code not in (200, 204):
            raise RuntimeError(f"could not rotate the SonarQube admin password: {response.status_code} {response.text[:200]}")


# Platform quality gate, applied to new code of every analysed commit. Coverage is not a
# condition: the analysis step compiles but does not run tests (tests run in the
# validation zone and the integration suites).
GATE = "sscp"
GATE_CONDITIONS = [
    ("new_software_quality_security_rating", "GT", "1"),
    ("new_software_quality_reliability_rating", "GT", "1"),
    ("new_software_quality_maintainability_rating", "GT", "1"),
    ("new_duplicated_lines_density", "GT", "3"),
]
ANALYSIS_USER = "ci-security"
INTERNAL_URL = "http://sonarqube.sscp.test:9000"


def _admin() -> "http.httpx.Client":
    return http.client("sonarqube", auth=("admin", admin_password()))


def _check(response, action: str) -> dict:
    if response.status_code >= 400:
        raise RuntimeError(f"SonarQube {action} failed: {response.status_code} {response.text[:200]}")
    return response.json() if response.content else {}


def configure_gate() -> None:
    with _admin() as client:
        gates = {g["name"]: g for g in _check(client.get("/api/qualitygates/list"), "list gates")["qualitygates"]}
        if GATE not in gates:
            _check(client.post("/api/qualitygates/create", data={"name": GATE}), "create gate")
        existing = {c["metric"]: c for c in _check(client.get("/api/qualitygates/show", params={"name": GATE}), "show gate")["conditions"]}
        wanted = {metric: (op, error) for metric, op, error in GATE_CONDITIONS}
        # SonarQube pre-fills new gates with its own conditions; keep exactly ours.
        for metric, condition in existing.items():
            if metric not in wanted:
                _check(client.post("/api/qualitygates/delete_condition", data={"id": condition["id"]}), f"remove {metric}")
            elif (condition["op"], condition["error"]) != wanted[metric]:
                _check(client.post("/api/qualitygates/update_condition", data={"id": condition["id"], "metric": metric, "op": wanted[metric][0], "error": wanted[metric][1]}), f"update {metric}")
        for metric, (op, error) in wanted.items():
            if metric not in existing:
                _check(client.post("/api/qualitygates/create_condition", data={"gateName": GATE, "metric": metric, "op": op, "error": error}), f"add {metric}")
        _check(client.post("/api/qualitygates/set_as_default", data={"name": GATE}), "set default gate")


def analysis_token() -> str:
    """Token of the analysis-only account used by the security zone; created on first use."""
    with _admin() as client:
        if client.get("/api/users/search", params={"q": ANALYSIS_USER}).json().get("users", []) == []:
            _check(client.post("/api/users/create", data={
                "login": ANALYSIS_USER, "name": "CI security zone", "local": "true",
                "password": credentials.get("sonarqube.user.ci-security", lambda: credentials.password(24) + "!9a"),
            }), "create analysis user")
        for permission in ("scan", "provisioning"):
            _check(client.post("/api/permissions/add_user", data={"login": ANALYSIS_USER, "permission": permission}), f"grant {permission}")

        stored = credentials.find("sonarqube.token.ci-security")
        if stored and http.client("sonarqube").get("/api/authentication/validate", auth=(stored, "")).json().get("valid"):
            return stored
        client.post("/api/user_tokens/revoke", data={"login": ANALYSIS_USER, "name": "security-zone"})
        token = _check(client.post("/api/user_tokens/generate", data={"login": ANALYSIS_USER, "name": "security-zone"}), "generate token")["token"]
        credentials.put("sonarqube.token.ci-security", token)
        return token
