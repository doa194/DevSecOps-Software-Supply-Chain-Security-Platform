"""Command line of the CI helper: `sscp-ci <command> ...` (see `sscp-ci --help`)."""
from __future__ import annotations

import argparse
import io
import subprocess
import sys
import tarfile
from pathlib import Path

from sscp_ci import artifacts, controlplane, dynamic, http, release, scanners, testenv, tools, zone


def cmd_image(args: argparse.Namespace) -> int:
    """Prints the mirrored, digest-pinned reference of a platform tool image."""
    images = tools.images()
    if args.name not in images:
        print(f"unknown tool image '{args.name}'; known: {', '.join(sorted(images))}", file=sys.stderr)
        return 2
    print(images[args.name]["image"])
    return 0


def cmd_fetch_source(args: argparse.Namespace) -> int:
    """Downloads one exact commit as an archive (no Git history, no credentials left behind)."""
    token = zone.secret("gitea")["token"]
    url = f"{zone.env('SSCP_GITEA_URL')}/api/v1/repos/{args.repository}/archive/{args.commit}.tar.gz"
    archive = http.request("GET", url, headers={"Authorization": f"token {token}"}, timeout=300).body
    destination = Path(args.dest)
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as tar:
        # Gitea prefixes every entry with "<repo>/"; strip it. The data filter refuses
        # absolute paths, links out of the tree and device files.
        members = []
        for member in tar.getmembers():
            _, _, relative = member.name.partition("/")
            if relative:
                member.name = relative
                members.append(member)
        tar.extractall(destination, members=members, filter="data")
    print(f"fetched {args.repository}@{args.commit} into {destination}")
    return 0


def _metadata(pairs: list[str] | None) -> dict[str, str]:
    return dict(pair.partition("=")[::2] for pair in pairs or [])


def cmd_submit(args: argparse.Namespace) -> int:
    """Uploads a raw scanner report as evidence for the build."""
    return controlplane.submit(args.build, args.commit, args.kind, Path(args.report), error=args.error,
                               deployable=args.deployable, digest=args.digest, metadata=_metadata(args.meta))


def cmd_scan(args: argparse.Namespace) -> int:
    """Runs one source scanner on a fetched commit and submits its report as evidence."""
    result = scanners.run(args.scanner, Path(args.src).resolve(), Path(args.out).resolve() / args.scanner)
    return controlplane.submit(args.build, args.commit, result.scanner.kind, result.report, error=result.error,
                               metadata={"toolName": result.scanner.tool, "toolVersion": result.version})


def cmd_check_dockerfile(args: argparse.Namespace) -> int:
    violations = artifacts.check_dockerfile(Path(args.src))
    for violation in violations:
        print(f"Dockerfile refused: {violation}")
    if not violations:
        print("Dockerfile uses only platform base images")
    return 1 if violations else 0


def cmd_build_images(args: argparse.Namespace) -> int:
    return artifacts.build_images(args.build, args.commit, Path(args.src).resolve(), Path(args.out).resolve())


def cmd_scan_images(args: argparse.Namespace) -> int:
    return artifacts.scan_images(args.build, args.commit, Path(args.out).resolve())


def cmd_quality(args: argparse.Namespace) -> int:
    return artifacts.code_quality(args.build, args.commit, Path(args.src).resolve(), Path(args.out).resolve())


def cmd_dynamic(args: argparse.Namespace) -> int:
    return dynamic.run(args.build, args.commit, Path(args.src).resolve(), Path(args.out).resolve())


def cmd_evaluate_source(args: argparse.Namespace) -> int:
    """Pull-request gate. Exit status 1 when the decision is FAIL."""
    decision = controlplane.request("POST", f"/api/builds/{args.build}/source-evaluation", json_body={"runId": zone.run_id()}).json()
    controlplane.print_decision(decision, "source security gate")
    return 1 if decision["outcome"] == "FAIL" else 0


def cmd_evaluate_build(args: argparse.Namespace) -> int:
    """Main-build trust decision for every artifact. Exit status 1 when any is FAIL."""
    decisions = controlplane.request("POST", f"/api/builds/{args.build}/evaluation", json_body={"runId": zone.run_id()}).json()
    for item in decisions:
        controlplane.print_decision(item["decision"], f"{item['deployable']}@{item['digest']}")
    return 1 if any(item["decision"]["outcome"] == "FAIL" for item in decisions) else 0


def cmd_complete(args: argparse.Namespace) -> int:
    controlplane.request("POST", f"/api/builds/{args.build}/completion",
                         json_body={"runId": zone.run_id(), "succeeded": args.status == "success", "reason": args.reason})
    print(f"build {args.build} marked {args.status}")
    return 0


def cmd_evaluate_release(args: argparse.Namespace) -> int:
    return release.evaluate(args.release)


def cmd_publish(args: argparse.Namespace) -> int:
    return release.publish(args.release, Path(args.out).resolve())


def cmd_fail_release(args: argparse.Namespace) -> int:
    return release.fail(args.release, args.reason)


def _build_arguments(parser: argparse.ArgumentParser, commit: bool = True, src: bool = False) -> None:
    parser.add_argument("--build", required=True)
    if commit:
        parser.add_argument("--commit", required=True)
    if src:
        parser.add_argument("--src", default="src")
    parser.add_argument("--out", default="reports")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="sscp-ci", description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)

    image = commands.add_parser("image", help="print a mirrored tool image reference")
    image.add_argument("name")
    image.set_defaults(handler=cmd_image)

    fetch = commands.add_parser("fetch-source", help="download one commit of a repository")
    fetch.add_argument("--repository", required=True)
    fetch.add_argument("--commit", required=True)
    fetch.add_argument("--dest", required=True)
    fetch.set_defaults(handler=cmd_fetch_source)

    submit = commands.add_parser("submit", help="upload a raw report as evidence")
    submit.add_argument("--build", required=True)
    submit.add_argument("--commit", required=True)
    submit.add_argument("--kind", required=True)
    submit.add_argument("--report", required=True)
    submit.add_argument("--error", help="the scanner did not complete: record the evidence as Failed")
    submit.add_argument("--deployable")
    submit.add_argument("--digest")
    submit.add_argument("--meta", action="append", help="metadata key=value (repeatable)")
    submit.set_defaults(handler=cmd_submit)

    scan = commands.add_parser("scan", help="run a source scanner and submit its report")
    scan.add_argument("scanner", choices=sorted(scanners.SCANNERS))
    _build_arguments(scan, src=True)
    scan.set_defaults(handler=cmd_scan)

    check = commands.add_parser("check-dockerfile", help="enforce the platform base-image policy")
    check.add_argument("--src", default="src")
    check.set_defaults(handler=cmd_check_dockerfile)

    build = commands.add_parser("build-images", help="build, push, register and SBOM every deployable (build zone)")
    _build_arguments(build, src=True)
    build.set_defaults(handler=cmd_build_images)

    scan_images = commands.add_parser("scan-images", help="Trivy and Grype for every registered candidate (security zone)")
    _build_arguments(scan_images)
    scan_images.set_defaults(handler=cmd_scan_images)

    quality = commands.add_parser("quality", help="SonarQube analysis and quality gate (security zone)")
    _build_arguments(quality, src=True)
    quality.set_defaults(handler=cmd_quality)

    dynamic_parser = commands.add_parser("dynamic", help="authorization suite and ZAP scan against the candidates (security zone)")
    _build_arguments(dynamic_parser, src=True)
    dynamic_parser.set_defaults(handler=cmd_dynamic)

    source = commands.add_parser("evaluate-source", help="pull-request source security gate")
    source.add_argument("--build", required=True)
    source.set_defaults(handler=cmd_evaluate_source)

    evaluate = commands.add_parser("evaluate-build", help="trust decision for every artifact of a main build")
    evaluate.add_argument("--build", required=True)
    evaluate.set_defaults(handler=cmd_evaluate_build)

    complete = commands.add_parser("complete", help="mark a build finished")
    complete.add_argument("--build", required=True)
    complete.add_argument("--status", required=True, choices=["success", "failure"])
    complete.add_argument("--reason")
    complete.set_defaults(handler=cmd_complete)

    evaluate_release = commands.add_parser("evaluate-release", help="final trust decision for a release (trust zone)")
    evaluate_release.add_argument("--release", required=True)
    evaluate_release.set_defaults(handler=cmd_evaluate_release)

    publish = commands.add_parser("publish", help="promote, sign and attest every image of an approved release and commit it to GitOps (trust zone)")
    publish.add_argument("--release", required=True)
    publish.add_argument("--out", default="attestations")
    publish.set_defaults(handler=cmd_publish)

    fail_release = commands.add_parser("fail-release", help="mark a release failed")
    fail_release.add_argument("--release", required=True)
    fail_release.add_argument("--reason", required=True)
    fail_release.set_defaults(handler=cmd_fail_release)

    args = parser.parse_args(argv)
    try:
        return args.handler(args)
    except (zone.ZoneError, http.HttpError, subprocess.CalledProcessError, artifacts.PipelineError, testenv.EnvironmentError,
            release.ReleaseError) as error:
        print(f"sscp-ci: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
