"""CI tool images in Harbor: the mirror of pinned upstream images and the ci-tools job image.

Pipelines pull every image from Harbor's `platform-tools` project, never from the
internet. Mirroring copies the linux/amd64 manifest that belongs to the pinned upstream
index digest; the copy has exactly that manifest's digest, so what runs can be traced back
to the pin in versions.yaml. The result is written to tool-images.json, which is baked into
the ci-tools image so workflows refer to tools by name (`sscp-ci image trivy`).
"""
from __future__ import annotations

import base64
import hashlib
import json
import re
import shutil
import tempfile
import time
from pathlib import Path

from sscp import paths, pki, shell, versions
from sscp.services import harbor

REGISTRY = "harbor.sscp.test:8443"
PROJECT = "platform-tools"
PLATFORM = "linux/amd64"
CI_DIR = paths.PLATFORM_REPO / "ci"
MANIFEST = paths.GENERATED_DIR / "tool-images.json"
CRANE_CONFIG_DIR = paths.SECRETS_DIR / "crane"
BUILD_DIR = paths.GENERATED_DIR / "ci-tools-build"


def repository_name(logical: str) -> str:
    """dotnetSdk -> dotnet-sdk."""
    return re.sub(r"(?<!^)(?=[A-Z])", "-", logical).lower()


def _crane(*args: str, check: bool = True, timeout: float = 1800, mounts: list[str] | None = None) -> str:
    """Runs crane on the edge network with the platform CA and Harbor credentials."""
    return registry_tool("crane", list(args), check=check, timeout=timeout, mounts=mounts)


def registry_tool(tool: str, args: list[str], *, check: bool = True, timeout: float = 1800, mounts: list[str] | None = None) -> str:
    """Runs a registry client image (crane, oras) on the edge network with the platform CA
    and Harbor administrator credentials (a docker config file in .local/secrets)."""
    CRANE_CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    auth = base64.b64encode(f"admin:{harbor.admin_password()}".encode()).decode()
    (CRANE_CONFIG_DIR / "config.json").write_text(json.dumps({"auths": {REGISTRY: {"auth": auth}}}), encoding="utf-8")
    command = [
        "docker", "run", "--rm", "--network", "sscp-edge",
        "--mount", f"type=bind,source={pki.CA_CERT.parent.resolve().as_posix()},target=/sscp-ca,readonly",
        "--mount", f"type=bind,source={CRANE_CONFIG_DIR.resolve().as_posix()},target=/docker,readonly",
        # Go reads every file in these directories: public roots plus the local CA.
        "-e", "SSL_CERT_DIR=/etc/ssl/certs:/sscp-ca", "-e", "DOCKER_CONFIG=/docker",
    ]
    for mount in mounts or []:
        command += ["--mount", mount]
    result = shell.run([*command, versions.image(tool), *args], check=check, timeout=timeout)
    return result.stdout.strip()


def _untagged(reference: str) -> str:
    """repo:tag@sha256:... -> repo@sha256:... (crane addresses by digest only)."""
    name, _, digest = reference.partition("@")
    repository = name.rsplit(":", 1)[0] if ":" in name.rsplit("/", 1)[-1] else name
    return f"{repository}@{digest}"


def _tag(reference: str) -> str:
    name = reference.partition("@")[0]
    last = name.rsplit("/", 1)[-1]
    return last.rsplit(":", 1)[1] if ":" in last else "pinned"


def _copy_with_retries(source: str, target: str, attempts: int = 4) -> None:
    """Large upstream downloads sometimes break off; crane skips blobs already copied."""
    for attempt in range(1, attempts + 1):
        try:
            _crane("copy", "--platform", PLATFORM, source, target)
            return
        except shell.CommandError:
            if attempt == attempts:
                raise
            time.sleep(10 * attempt)


def mirror() -> dict[str, str]:
    """Copies every image listed under ciMirror into Harbor; returns logical name -> reference."""
    harbor.HarborApi().ensure_project(PROJECT, public=True)
    previous = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else {}
    result: dict[str, str] = {}
    for logical in versions.load()["ciMirror"]:
        upstream = versions.image(logical)
        target_repository = f"{REGISTRY}/{PROJECT}/{repository_name(logical)}"
        known = previous.get(logical, {})
        if known.get("upstream") == upstream and _crane("manifest", known["reference"], check=False, timeout=60):
            result[logical] = known["reference"]
            continue
        platform_digest = _crane("digest", "--platform", PLATFORM, _untagged(upstream), timeout=120)
        _copy_with_retries(_untagged(upstream), f"{target_repository}:{_tag(upstream)}")
        copied = _crane("digest", f"{target_repository}:{_tag(upstream)}", timeout=60)
        if copied != platform_digest:
            raise RuntimeError(f"{logical}: Harbor has {copied}, expected {platform_digest} from {upstream}")
        result[logical] = f"{target_repository}@{platform_digest}"
        _record(logical, result[logical], upstream, _tag(upstream))
    return result


def references() -> dict[str, str]:
    return {logical: entry["reference"] for logical, entry in json.loads(MANIFEST.read_text(encoding="utf-8")).items()}


def job_manifest() -> dict[str, dict[str, str]]:
    """The tool list baked into ci-tools: tool name -> image reference and version."""
    entries = json.loads(MANIFEST.read_text(encoding="utf-8"))
    return {
        repository_name(logical): {"image": entry["reference"], "version": entry.get("version") or _tag(entry["upstream"])}
        for logical, entry in entries.items()
    }


def _record(logical: str, reference: str, upstream: str, version: str) -> None:
    entries = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else {}
    entries[logical] = {"upstream": upstream, "reference": reference, "version": version}
    MANIFEST.write_text(json.dumps(entries, indent=2), encoding="utf-8")


def publish_built_image(name: str, context: Path, build_args: dict[str, str]) -> str:
    """Builds an image from a prepared context and pushes it to platform-tools.

    The tag is a hash of every input (files and build arguments), so an unchanged image is
    not rebuilt and a changed one gets a new tag. Returns the digest reference.
    """
    fingerprint = hashlib.sha256()
    for file in sorted(p for p in context.rglob("*") if p.is_file()):
        fingerprint.update(file.relative_to(context).as_posix().encode())
        fingerprint.update(file.read_bytes())
    fingerprint.update(json.dumps(build_args, sort_keys=True).encode())
    tag = fingerprint.hexdigest()[:16]
    target = f"{REGISTRY}/{PROJECT}/{name}:{tag}"

    existing = _crane("digest", target, check=False, timeout=60)
    if existing.startswith("sha256:"):
        return f"{REGISTRY}/{PROJECT}/{name}@{existing}"

    local_tag = f"sscp/{name}:{tag}"
    command = ["docker", "build", "--platform", PLATFORM, "-t", local_tag]
    for arg, value in build_args.items():
        command += ["--build-arg", f"{arg}={value}"]
    shell.run([*command, str(context)], timeout=1800)
    # Pushed with crane from inside the platform network: the host Docker daemon never
    # needs to log in to Harbor.
    with tempfile.TemporaryDirectory(prefix=f"sscp-{name}-") as scratch:
        archive = Path(scratch) / "image.tar"
        shell.run(["docker", "save", "-o", str(archive), local_tag], timeout=600)
        pushed = _crane("push", "/work/image.tar", target,
                        mounts=[f"type=bind,source={Path(scratch).resolve().as_posix()},target=/work,readonly"], timeout=900)
    return f"{REGISTRY}/{PROJECT}/{name}@{pushed.rsplit('@', 1)[-1]}"


def publish_sonar_dotnet() -> str:
    """The SonarQube analysis image for .NET (SDK + Java runtime + SonarScanner for .NET)."""
    context = paths.GENERATED_DIR / "sonar-dotnet-build"
    if context.exists():
        shutil.rmtree(context)
    context.mkdir(parents=True)
    shutil.copy2(CI_DIR / "sonar" / "Dockerfile", context / "Dockerfile")
    version = versions.tool("sonarScannerDotnet")
    reference = publish_built_image("sonar-dotnet", context, {
        "SDK_IMAGE": versions.image("dotnetSdk"), "JRE_IMAGE": versions.image("temurinJre"), "SCANNER_VERSION": version,
    })
    _record("sonarDotnet", reference, f"built:sonar-dotnet:{version}", version)
    return reference


def publish_ci_tools() -> str:
    """Builds the ci-tools job image (helper, rules, tool list, local CA) and pushes it."""
    if BUILD_DIR.exists():
        shutil.rmtree(BUILD_DIR)
    shutil.copytree(CI_DIR / "sscp_ci", BUILD_DIR / "sscp_ci", ignore=shutil.ignore_patterns("__pycache__"))
    shutil.copytree(CI_DIR / "rules", BUILD_DIR / "rules")
    shutil.copy2(CI_DIR / "Dockerfile", BUILD_DIR / "Dockerfile")
    shutil.copy2(CI_DIR / "requirements.txt", BUILD_DIR / "requirements.txt")
    shutil.copy2(pki.CA_CERT, BUILD_DIR / "ca.crt")
    (BUILD_DIR / "tool-images.json").write_text(json.dumps(job_manifest(), indent=2, sort_keys=True), encoding="utf-8")
    return publish_built_image("ci-tools", BUILD_DIR, {
        "PYTHON_IMAGE": versions.image("python"), "DOCKER_CLI_IMAGE": versions.image("dockerCli"),
        "COSIGN_IMAGE": versions.image("cosign"), "CRANE_IMAGE": versions.image("crane"),
    })
