"""Command-line entry point: `sscp <command>`.

Each command lives in its own module; this file only wires arguments to them so the
list of available operations is visible in one place.
"""
from __future__ import annotations

import argparse
import sys

from sscp import console, doctor, lifecycle, sourcecontrol, verify, vulndb, workload
from sscp.shell import CommandError


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="sscp", description="Software Supply Chain Security Platform automation")
    commands = parser.add_subparsers(dest="command", required=True)

    commands.add_parser("doctor", help="check workstation prerequisites and pinning policy")

    up = commands.add_parser("up", help="start and configure the software factory")
    up.add_argument(
        "--with", dest="capabilities", default="",
        help=f"comma-separated optional capabilities: {', '.join(lifecycle.OPTIONAL)}",
    )

    commands.add_parser("down", help="stop platform services, keeping their data")
    reset = commands.add_parser("reset", help="delete all platform containers, volumes and generated credentials")
    reset.add_argument("--yes", action="store_true", help="confirm that all local platform data may be deleted")
    commands.add_parser("status", help="show platform container state")

    workload_parser = commands.add_parser("workload", help="run the commerce workload on the host for development")
    workload_parser.add_argument("action", choices=["setup", "start", "stop", "reset"])

    repo_parser = commands.add_parser("repo", help="publish workspace changes to Gitea")
    repo_parser.add_argument("action", choices=["sync", "propose", "approve", "merge", "tag", "build"],
                             help="sync: commit the platform repository as pat; propose: open a pull request; approve/merge: review "
                                  "or merge pull request -n; tag: push a release tag on commerce-app as the release manager; "
                                  "build: have the Control Plane build the tip of commerce-app main (after a fresh start)")
    repo_parser.add_argument("-m", "--message", default="Update from the workspace")
    repo_parser.add_argument("-b", "--branch", help="branch for `propose`")
    repo_parser.add_argument("--as", dest="user", help="account for `propose` (default alice) or `tag` (default rhea)")
    repo_parser.add_argument("-t", "--tag", help="tag name for `tag`, for example v1.0.0")
    repo_parser.add_argument("--commit", help="commit for `tag` (default: tip of main)")
    repo_parser.add_argument("--repo", choices=["commerce-app", "commerce-gitops"], default="commerce-app",
                             help="repository for `propose`, `approve` and `merge` (default commerce-app)")
    repo_parser.add_argument("-n", "--number", type=int, help="pull request number for `approve` and `merge`")

    tools_parser = commands.add_parser("tools", help="maintain CI tool data")
    tools_parser.add_argument("action", choices=["refresh-db"], help="refresh-db: copy current Trivy and Grype databases into Harbor")

    verify_parser = commands.add_parser("verify", help="run operational verification suites against the running platform")
    verify_parser.add_argument("suites", nargs="*", help=f"suites to run (default: all): {', '.join(verify.SUITES)}")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        match args.command:
            case "doctor":
                return doctor.run()
            case "up":
                return lifecycle.up([c for c in args.capabilities.split(",") if c])
            case "down":
                return lifecycle.down()
            case "reset":
                if not args.yes:
                    print("refusing to delete platform data without --yes", file=sys.stderr)
                    return 2
                return lifecycle.reset()
            case "status":
                return lifecycle.status()
            case "verify":
                return verify.run(args.suites)
            case "tools":
                refreshed = vulndb.refresh()
                console.ok(f"vulnerability databases refreshed: trivy {refreshed['trivy'][:19]}, grype built {refreshed['grype']}")
                return 0
            case "repo" if args.action == "sync":
                changed = sourcecontrol.sync_platform(args.message)
                console.ok("platform repository updated" if changed else "platform repository already up to date")
                return 0
            case "repo" if args.action in ("approve", "merge"):
                if not args.number:
                    print(f"`sscp repo {args.action}` needs -n <pull request number>", file=sys.stderr)
                    return 2
                reviewer = "omar" if args.repo == "commerce-gitops" else "max"
                if args.action == "approve":
                    sourcecontrol.approve(args.repo, args.number, args.user or reviewer)
                    console.ok(f"pull request #{args.number} approved as {args.user or reviewer}")
                else:
                    author = "pat" if args.repo == "commerce-gitops" else "alice"
                    commit = sourcecontrol.merge(args.repo, args.number, args.user or author)
                    console.ok(f"pull request #{args.number} merged as {commit[:12]}")
                return 0
            case "repo" if args.action == "build":
                commit = sourcecontrol.announce_main("commerce-app")
                console.ok(f"push event for main ({commit[:12]}) sent; the Control Plane starts the main pipeline unless it built this commit already")
                return 0
            case "repo" if args.action == "tag":
                if not args.tag:
                    print("`sscp repo tag` needs --tag", file=sys.stderr)
                    return 2
                commit = sourcecontrol.push_tag(args.tag, args.commit, user=args.user or "rhea")
                console.ok(f"tag {args.tag} pushed on {commit[:12]}; the Control Plane starts the release pipeline")
                return 0
            case "repo":
                if not args.branch:
                    print("`sscp repo propose` needs --branch", file=sys.stderr)
                    return 2
                user = args.user or ("pat" if args.repo == "commerce-gitops" else "alice")
                number = sourcecontrol.propose(args.branch, args.message, user=user, repository=args.repo)
                console.ok(f"pull request #{number}: https://localhost:3000/{sourcecontrol.PROPOSABLE[args.repo][0]}/pulls/{number}")
                return 0
            case "workload":
                return {"setup": workload.setup, "start": workload.start, "stop": workload.stop, "reset": workload.reset}[args.action]()
    except (CommandError, RuntimeError, TimeoutError) as error:
        print(f"\n{error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130
    return 2
