"""Source scanners as the platform runs them: pinned image, arguments, report and evidence kind.

Keeping the invocations here (instead of in workflow YAML) means every pipeline runs a
scanner the same way, and a change to how scanning works is a reviewed change to the
platform repository. Source scanners get no network access.

A scanner "fails" only when it could not do its job (crash, bad configuration, missing
report). Findings never fail a step: the Security Control Plane reads the report and
decides.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from sscp_ci import tools

RULES = Path(__file__).resolve().parent.parent / "rules"

# Scanner configuration and ignore files a repository could ship to weaken its own scan.
# They are deleted from the job's copy of the source before any scanner runs; only the
# platform's rules and the Control Plane's policy decide what is reported.
SUPPRESSION_FILES = (
    ".gitleaks.toml", ".gitleaksignore",
    ".checkov.yaml", ".checkov.yml",
    ".hadolint.yaml", ".hadolint.yml",
    ".semgrepignore",
    ".trivyignore", ".trivyignore.yaml",
)


@dataclass(frozen=True)
class Scanner:
    name: str
    kind: str
    tool: str
    report: str                       # file name under the output directory
    args: Callable[[], list[str]]
    uses_rules: bool = False
    stdout_is_report: bool = False    # the tool prints its report instead of writing a file
    ok_exit_codes: tuple[int, ...] = (0,)


SCANNERS = {
    "secrets": Scanner(
        "secrets", "SecretScan", "gitleaks", "gitleaks.json",
        # --redact: the report must never become a second copy of a leaked secret.
        # The platform's configuration only: a repository's .gitleaks.toml, .gitleaksignore
        # and inline `gitleaks:allow` comments cannot suppress findings.
        lambda: ["dir", "/work/src", "--config", "/work/rules/gitleaks/gitleaks.toml", "--ignore-gitleaks-allow",
                 "--gitleaks-ignore-path", "/work/rules/gitleaks", "--redact", "--no-banner", "--report-format", "json",
                 "--report-path", "/work/out/gitleaks.json", "--exit-code", "0"],
        uses_rules=True,
    ),
    "sast": Scanner(
        "sast", "StaticAnalysis", "semgrep", "semgrep.sarif",
        # Only the platform's own rules, read from the job, never from the internet.
        # --disable-nosem: inline suppressions in application code are ignored.
        lambda: ["semgrep", "scan", "--config", "/work/rules/semgrep", "--sarif", "--output", "/work/out/semgrep.sarif",
                 "--metrics", "off", "--disable-version-check", "--disable-nosem", "--no-git-ignore", "--quiet", "/work/src"],
        uses_rules=True,
    ),
    "iac": Scanner(
        "iac", "InfrastructureScan", "checkov", "results_json.json",
        # Not --quiet: checks skipped by inline `checkov:skip` comments must stay in the
        # report, where the Control Plane counts them as failed.
        lambda: ["-d", "/work/src", "--framework", "dockerfile", "kubernetes", "kustomize", "--output", "json",
                 "--output-file-path", "/work/out", "--soft-fail", "--skip-download"],
    ),
    # GitOps repositories: Checkov renders every environment overlay with Kustomize (its
    # image carries the binary) and checks the Kubernetes objects that would be applied.
    "deployment": Scanner(
        "deployment", "DeploymentConfigScan", "checkov", "results_json.json",
        lambda: ["-d", "/work/src/overlays", "--framework", "kustomize", "--output", "json",
                 "--output-file-path", "/work/out", "--soft-fail", "--skip-download"],
    ),
    "dockerfile": Scanner(
        "dockerfile", "DockerfileLint", "hadolint", "hadolint.json",
        # --disable-ignore-pragma: `# hadolint ignore=` comments in the Dockerfile are ignored.
        lambda: ["hadolint", "--format", "json", "--no-fail", "--disable-ignore-pragma", "/work/src/Dockerfile"],
        stdout_is_report=True,
    ),
}


@dataclass
class ScanResult:
    scanner: Scanner
    report: Path
    completed: bool
    error: str | None
    version: str


def run(name: str, source: Path, output: Path) -> ScanResult:
    scanner = SCANNERS[name]
    inputs = {"src": source}
    if scanner.uses_rules:
        inputs["rules"] = RULES
    result = tools.run(tools.ToolRun(scanner.tool, scanner.args(), inputs=inputs, network="none", remove=SUPPRESSION_FILES), output)
    report = output / scanner.report
    if scanner.stdout_is_report and result.exit_code in scanner.ok_exit_codes:
        report.write_text(result.stdout, encoding="utf-8")

    error = None
    if result.exit_code not in scanner.ok_exit_codes:
        error = f"{scanner.tool} exited with {result.exit_code}: {result.stderr.strip()[-400:]}"
    elif not report.exists() or report.stat().st_size == 0:
        error = f"{scanner.tool} produced no report"
    return ScanResult(scanner, report, error is None, error, tools.version(scanner.tool))
