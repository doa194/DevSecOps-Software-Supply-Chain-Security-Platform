"""Dynamic security testing of a main build's candidate images (security zone).

1. Start the candidates with their data services in a private test environment.
2. Run the platform's authorization suite through the gateway -> SecurityTests evidence.
3. Run an authenticated OWASP ZAP API scan through the gateway -> DynamicScan evidence.
4. Remove the environment.
If the environment cannot start, both kinds are reported as failed executions, so the
trust decision names the missing control instead of passing it.
"""
from __future__ import annotations

import json
import socket
import subprocess
from pathlib import Path

from sscp_ci import controlplane, http, tools, zone
from sscp_ci.authz_suite import Suite
from sscp_ci.testenv import TestEnvironment

GATEWAY = "http://commerce-gateway:8080"
DAST_TOKEN_URL = "https://keycloak.sscp.test:9443/realms/commerce/protocol/openid-connect/token"
SCAN_MINUTES = 10


def _zap(environment: TestEnvironment, token: str, reports: Path) -> tuple[Path, str | None]:
    out = reports / "zap"
    result = tools.run(tools.ToolRun(
        "zap",
        # The API description comes from the API; requests go through the gateway, as a
        # client's would (the override needs a scheme, or ZAP takes the host for one).
        # -silent: ZAP makes no requests of its own (no update checks).
        ["zap-api-scan.py", "-t", "http://commerce-api:8080/openapi/v1.json", "-f", "openapi", "-O", "http://commerce-gateway:8080",
         "-J", "out/zap.json", "-I", "-z", f"-silent -config scanner.maxScanDurationInMins={SCAN_MINUTES}"],
        network=environment.network, mount="/zap/wrk",
        # ZAP adds this header to every request it sends to the gateway (authenticated scan).
        env={"ZAP_AUTH_HEADER": "Authorization", "ZAP_AUTH_HEADER_VALUE": f"Bearer {token}", "ZAP_AUTH_HEADER_SITE": "commerce-gateway"},
    ), out)
    report = out / "zap.json"
    # 0, 1 and 2 mean the scan completed (with or without alerts); findings are for the Control Plane.
    completed = result.exit_code in (0, 1, 2) and report.exists()
    if not completed:
        print(*(result.stdout + result.stderr).strip().splitlines()[-60:], sep="\n", flush=True)
    return report, None if completed else f"zap exited {result.exit_code}: {(result.stdout + result.stderr).strip()[-500:]}"


def run(build_id: str, commit: str, source: Path, reports: Path) -> int:
    plan = controlplane.plan(build_id)
    images = {artifact["deployable"]: artifact["reference"] for artifact in plan["artifacts"]}
    test = zone.secret("commerce-test")
    passwords = json.loads(test["personas"])
    registry = zone.secret("harbor")
    reports.mkdir(parents=True, exist_ok=True)
    tests_report, zap_report = reports / "security-tests.json", reports / "zap" / "zap.json"
    environment = TestEnvironment(images, source / "config" / "publishers.yaml", test["identity_admin_secret"])
    job_container = socket.gethostname()
    setup_error = tests_error = zap_error = None
    try:
        subprocess.run(["docker", "login", registry["registry"], "--username", registry["username"], "--password-stdin"],
                       input=registry["password"], text=True, capture_output=True, check=True)
        print("starting the security-test environment ...", flush=True)
        environment.start()
        environment.join(job_container)
        print("environment ready; running the authorization suite", flush=True)
        suite = Suite(GATEWAY, passwords)
        try:
            suite.run()
        except Exception as error:  # reported as a failed execution below
            tests_error = f"the authorization suite could not run: {error}"[:900]
        tests_report.write_text(json.dumps(suite.report(GATEWAY), indent=2), encoding="utf-8")

        token = http.request("POST", DAST_TOKEN_URL, form={
            "grant_type": "password", "client_id": "commerce-dast", "username": "ada", "password": passwords["ada"]}).json()["access_token"]
        print(f"running the ZAP API scan (at most {SCAN_MINUTES} minutes of active scanning)", flush=True)
        zap_report, zap_error = _zap(environment, token, reports)
    except Exception as error:  # the environment itself failed: both controls did not run
        setup_error = f"security-test environment failed: {error}"[:900]
        print(setup_error, flush=True)
    finally:
        environment.stop(job_container)

    failures = controlplane.submit(build_id, commit, "SecurityTests", tests_report, error=setup_error or tests_error,
                                   metadata={"toolName": "sscp-authorization-suite", "toolVersion": "1"})
    failures += controlplane.submit(build_id, commit, "DynamicScan", zap_report, error=setup_error or zap_error,
                                    metadata={"toolName": "zap", "toolVersion": tools.version("zap")})
    return 1 if failures else 0
