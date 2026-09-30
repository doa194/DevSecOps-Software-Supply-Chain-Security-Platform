"""Build-zone and security-zone work on images: build, push, register, SBOM, scan, quality.

Build zone: builds every registered deployable from the fetched commit with the platform's
own base images (from Harbor, by digest), pushes it to the candidates project, registers
the digest with the Control Plane and submits an SBOM made from the pushed image.

Security zone: scans every registered candidate digest with Trivy (blocking) and Grype
(secondary), using the vulnerability databases mirrored in Harbor, and runs the SonarQube
analysis of the commit.
"""
from __future__ import annotations

import base64
import json
import re
import subprocess
import time
from pathlib import Path

from sscp_ci import controlplane, dockerfile_policy, http, tools, zone

REGISTRY = "harbor.sscp.test:8443"
TRIVY_DB = f"{REGISTRY}/platform-tools/trivy-db:2"
GRYPE_DB = f"{REGISTRY}/platform-tools/grype-db:v6"
CA_DIR = Path("/usr/local/share/ca-certificates")   # the platform CA inside ci-tools
DEPLOYABLE = re.compile(r"^[a-z][a-z0-9-]{1,40}$")


class PipelineError(RuntimeError):
    pass


def _docker(*args: str, input_text: str | None = None) -> str:
    result = subprocess.run(["docker", *args], input=input_text, capture_output=True, text=True)
    if result.returncode != 0:
        raise PipelineError(f"docker {args[0]} failed: {result.stderr.strip()[-800:]}")
    return result.stdout


def _registry_login(credentials: dict[str, str]) -> None:
    _docker("login", credentials["registry"], "--username", credentials["username"], "--password-stdin", input_text=credentials["password"])


# ---------------------------------------------------------------- build zone

def check_dockerfile(source: Path) -> list[str]:
    return dockerfile_policy.violations((source / "Dockerfile").read_text(encoding="utf-8"))


def build_images(build_id: str, commit: str, source: Path, reports: Path) -> int:
    violations = check_dockerfile(source)
    if violations:
        for violation in violations:
            print(f"Dockerfile refused: {violation}")
        return 1

    plan = controlplane.plan(build_id)
    if plan["commit"] != commit:
        raise PipelineError(f"the build is for {plan['commit']}, not {commit}")
    credentials = zone.secret("harbor")
    _registry_login(credentials)
    build_args = {
        "SDK_IMAGE": tools.image("dotnet-sdk"),
        "RUNTIME_IMAGE": tools.image("dotnet-aspnet"),
        # The Dockerfile's `# syntax=` frontend, taken from the mirror instead of Docker Hub.
        "BUILDKIT_SYNTAX": tools.image("dockerfile-frontend"),
    }
    failures = 0
    for deployable in plan["deployables"]:
        if not DEPLOYABLE.match(deployable):
            raise PipelineError(f"unexpected deployable name {deployable!r}")
        repository = f"{plan['candidateRepository']}/{deployable}"
        reference = f"{repository}:{commit}"
        command = ["build", "--platform", "linux/amd64", "--target", deployable, "--tag", reference,
                   # A plain image manifest; the platform makes its own SBOM and provenance.
                   "--provenance=false", "--sbom=false",
                   "--label", f"org.opencontainers.image.revision={commit}",
                   "--label", f"org.opencontainers.image.source=https://gitea.sscp.test:3000/{plan['sourceRepository']}"]
        for name, value in build_args.items():
            command += ["--build-arg", f"{name}={value}"]
        print(f"building {deployable} ...", flush=True)
        _docker(*command, str(source))
        pushed = _docker("push", reference)
        digest = re.findall(r"digest: (sha256:[0-9a-f]{64})", pushed)
        if not digest:
            raise PipelineError(f"could not read the pushed digest of {reference}")
        artifact = controlplane.register_artifact(build_id, deployable, repository, digest[-1])
        print(f"registered {artifact['reference']}")
        failures += sbom(build_id, commit, deployable, repository, digest[-1], credentials, reports)
    return 1 if failures else 0


def sbom(build_id: str, commit: str, deployable: str, repository: str, digest: str, credentials: dict[str, str], reports: Path) -> int:
    """CycloneDX SBOM of the pushed image (not of the local build), so it describes the digest."""
    output = reports / f"sbom-{deployable}"
    result = tools.run(tools.ToolRun(
        "syft", [f"registry:{repository}@{digest}", "--output", "cyclonedx-json=/work/out/sbom.cdx.json", "--quiet"],
        inputs={"ca": CA_DIR}, network="bridge",
        env={"SSL_CERT_DIR": "/etc/ssl/certs:/work/ca", "SYFT_REGISTRY_AUTH_AUTHORITY": credentials["registry"],
             "SYFT_REGISTRY_AUTH_USERNAME": credentials["username"], "SYFT_REGISTRY_AUTH_PASSWORD": credentials["password"]},
    ), output)
    report = output / "sbom.cdx.json"
    error = None if result.exit_code == 0 and report.exists() else f"syft exited {result.exit_code}: {result.stderr.strip()[-400:]}"
    return controlplane.submit(build_id, commit, "Sbom", report, error=error, deployable=deployable, digest=digest,
                               metadata={"toolName": "syft", "toolVersion": tools.version("syft")})


# ---------------------------------------------------------------- security zone

def _trivy_database() -> dict[str, str]:
    result = tools.run(tools.ToolRun("trivy", ["version", "--format", "json", "--cache-dir", "/cache"],
                                     volumes={"sscp-trivy-cache": "/cache"}), Path("/tmp/trivy-version"))
    info = json.loads(result.stdout or "{}").get("VulnerabilityDB") or {}
    return {"databaseVersion": str(info.get("Version", "")), "databaseUpdatedAt": info.get("UpdatedAt", "")}


def _prepare_grype_database(reports: Path) -> str | None:
    """Pulls the Grype database from Harbor and imports it into the zone's cache volume."""
    download = reports / "grype-db"
    pulled = tools.run(tools.ToolRun("oras", ["pull", GRYPE_DB, "--output", "/work/out"], inputs={"ca": CA_DIR}, network="bridge",
                                     env={"SSL_CERT_DIR": "/etc/ssl/certs:/work/ca"}), download)
    archives = list(download.glob("*.tar.zst"))
    if pulled.exit_code != 0 or not archives:
        return f"could not pull the Grype database: {pulled.stderr.strip()[-300:]}"
    imported = tools.run(tools.ToolRun("grype", ["db", "import", f"/work/db/{archives[0].name}"], inputs={"db": download},
                                       volumes={"sscp-grype-cache": "/cache"}, env={"GRYPE_DB_CACHE_DIR": "/cache"}), reports / "grype-import")
    return None if imported.exit_code == 0 else f"grype db import failed: {imported.stderr.strip()[-300:]}"


def scan_images(build_id: str, commit: str, reports: Path) -> int:
    plan = controlplane.plan(build_id)
    if not plan["artifacts"]:
        raise PipelineError("the build has no registered artifacts to scan")
    credentials = zone.secret("harbor")
    registry_env = {"SSL_CERT_DIR": "/etc/ssl/certs:/work/ca"}

    # Trivy: download (from Harbor) or refresh the database once, then scan offline.
    tools.run(tools.ToolRun("trivy", ["image", "--download-db-only", "--db-repository", TRIVY_DB, "--cache-dir", "/cache"],
                            inputs={"ca": CA_DIR}, network="bridge", env=registry_env, volumes={"sscp-trivy-cache": "/cache"}),
              reports / "trivy-db")
    database = _trivy_database()
    grype_error = _prepare_grype_database(reports)

    failures = 0
    for artifact in plan["artifacts"]:
        reference, deployable, digest = artifact["reference"], artifact["deployable"], artifact["digest"]
        out = reports / f"trivy-{deployable}"
        trivy = tools.run(tools.ToolRun("trivy", [
            "image", "--image-src", "remote", "--scanners", "vuln", "--format", "json", "--output", "/work/out/trivy.json",
            "--cache-dir", "/cache", "--skip-db-update", "--skip-java-db-update", "--offline-scan", "--timeout", "15m", reference,
        ], inputs={"ca": CA_DIR}, network="bridge", volumes={"sscp-trivy-cache": "/cache"},
            env={**registry_env, "TRIVY_USERNAME": credentials["username"], "TRIVY_PASSWORD": credentials["password"]}), out)
        error = None if trivy.exit_code == 0 and (out / "trivy.json").exists() else f"trivy exited {trivy.exit_code}: {trivy.stderr.strip()[-400:]}"
        failures += controlplane.submit(build_id, commit, "VulnerabilityScan", out / "trivy.json", error=error, deployable=deployable,
                                        digest=digest, metadata={"toolName": "trivy", "toolVersion": tools.version("trivy"), **database})

        out = reports / f"grype-{deployable}"
        grype = None if grype_error else tools.run(tools.ToolRun("grype", [
            f"registry:{reference}", "--output", "json=/work/out/grype.json", "--quiet",
        ], inputs={"ca": CA_DIR}, network="bridge", volumes={"sscp-grype-cache": "/cache"}, env={
            **registry_env, "GRYPE_DB_CACHE_DIR": "/cache", "GRYPE_DB_AUTO_UPDATE": "false",
            # The trust policy allows seven days; Grype's own default is five.
            "GRYPE_DB_MAX_ALLOWED_BUILT_AGE": "168h",
            "GRYPE_REGISTRY_AUTH_AUTHORITY": credentials["registry"],
            "GRYPE_REGISTRY_AUTH_USERNAME": credentials["username"], "GRYPE_REGISTRY_AUTH_PASSWORD": credentials["password"],
        }), out)
        error = grype_error or (None if grype.exit_code == 0 and (out / "grype.json").exists() else f"grype exited {grype.exit_code}: {grype.stderr.strip()[-400:]}")
        failures += controlplane.submit(build_id, commit, "SecondaryVulnerabilityScan", out / "grype.json", error=error,
                                        deployable=deployable, digest=digest, metadata={"toolName": "grype", "toolVersion": tools.version("grype")})
    return 1 if failures else 0


def code_quality(build_id: str, commit: str, source: Path, reports: Path) -> int:
    """SonarQube analysis of the commit; the quality gate status is the evidence."""
    plan = controlplane.plan(build_id)
    sonar = zone.secret("sonarqube")
    solution = next(iter(sorted(p.name for p in source.iterdir() if p.suffix in (".slnx", ".sln"))), None)
    out = reports / "sonarqube"
    error = None
    if solution is None:
        error = "no solution file at the repository root"
    else:
        project = plan["application"]
        script = " && ".join([
            "cd /work/src",
            f"dotnet sonarscanner begin /k:{project} /v:{commit} /d:sonar.host.url={sonar['url']} /d:sonar.token=\"$SONAR_TOKEN\" /d:sonar.scm.disabled=true",
            f"dotnet build {solution} --configuration Release",
            "dotnet sonarscanner end /d:sonar.token=\"$SONAR_TOKEN\"",
            "cp .sonarqube/out/.sonar/report-task.txt /work/out/",
        ])
        result = tools.run(tools.ToolRun("sonar-dotnet", ["bash", "-c", script], inputs={"src": source}, network="bridge",
                                         env={"SONAR_TOKEN": sonar["token"], "CI": "true"}), out)
        task = out / "report-task.txt"
        if result.exit_code != 0 or not task.exists():
            error = f"analysis failed ({result.exit_code}): {(result.stdout + result.stderr).strip()[-600:]}"
        else:
            error = _wait_for_gate(sonar, task, out / "quality-gate.json")
    return controlplane.submit(build_id, commit, "CodeQuality", out / "quality-gate.json", error=error,
                               metadata={"toolName": "sonarqube", "toolVersion": tools.version("sonar-dotnet")})


def _wait_for_gate(sonar: dict[str, str], task_file: Path, destination: Path, timeout: float = 600) -> str | None:
    properties = dict(line.split("=", 1) for line in task_file.read_text(encoding="utf-8").splitlines() if "=" in line)
    auth = {"Authorization": "Basic " + base64.b64encode(f"{sonar['token']}:".encode()).decode()}
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        task = http.request("GET", f"{sonar['url']}/api/ce/task?id={properties['ceTaskId']}", headers=auth).json()["task"]
        if task["status"] == "SUCCESS":
            gate = http.request("GET", f"{sonar['url']}/api/qualitygates/project_status?analysisId={task['analysisId']}", headers=auth)
            destination.write_bytes(gate.body)
            return None
        if task["status"] in ("FAILED", "CANCELED"):
            return f"SonarQube could not process the analysis ({task['status']})"
        time.sleep(3)
    return "SonarQube did not finish processing the analysis in time"
