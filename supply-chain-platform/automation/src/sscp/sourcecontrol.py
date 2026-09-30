"""Makes Gitea match platform/gitea/source-control.yaml (`sscp up`).

Creates people and machine identities, organisations, repositories, teams, branch and tag
protections and the webhook to the Security Control Plane. Re-running converges instead of
failing. A repository is published from its workspace directory only while it is still
empty; after that it changes through Git like any other repository.
"""
from __future__ import annotations

import base64
import tempfile
from pathlib import Path
from typing import Any

import yaml

from sscp import credentials, paths, pki, shell
from sscp.services import gitea

DEFINITION = paths.PLATFORM_DIR / "gitea" / "source-control.yaml"
WEBHOOK_URL = "https://controlplane.sscp.test:8443/api/webhooks/gitea"
WEBHOOK_EVENTS = ["push", "pull_request"]

# Units a people team gets. Actions stay read-only so a team cannot re-run or dispatch
# workflows beyond what a pull request already triggers.
PEOPLE_UNITS = ["repo.code", "repo.pulls", "repo.issues", "repo.releases"]


def user_password(name: str) -> str:
    return credentials.get(f"gitea.user.{name}", lambda: credentials.password(24))


def webhook_secret() -> str:
    return credentials.get("controlplane.webhook-secret", lambda: credentials.hex_token(32))


def load() -> dict[str, Any]:
    return yaml.safe_load(DEFINITION.read_text(encoding="utf-8"))


def apply() -> None:
    definition = load()
    api = gitea.Gitea()
    for name, spec in definition["users"].items():
        _ensure_user(api, name, spec)
    for org, spec in definition["organisations"].items():
        _ensure_org(api, org, spec)
        for repo, repo_spec in spec.get("repositories", {}).items():
            _ensure_repository(api, org, repo, repo_spec)
        for team, team_spec in spec.get("teams", {}).items():
            _ensure_team(api, org, team, team_spec)
        for repo, repo_spec in spec.get("repositories", {}).items():
            _ensure_protections(api, org, repo, repo_spec)
            if repo_spec.get("webhook") == "controlplane":
                _ensure_webhook(api, org, repo)


def _ensure_user(api: gitea.Gitea, name: str, spec: dict) -> None:
    if api.get(f"users/{name}") is None:
        api.post("admin/users", {
            "username": name, "email": f"{name}@sscp.test", "full_name": spec.get("fullName", name),
            "password": user_password(name), "must_change_password": False, "send_notify": False,
            "visibility": "private",
            # Machine identities only see what they are explicitly given.
            "restricted": bool(spec.get("machine")),
        })


def _ensure_org(api: gitea.Gitea, org: str, spec: dict) -> None:
    if api.get(f"orgs/{org}") is None:
        api.post("orgs", {"username": org, "description": spec.get("description", ""), "visibility": "private",
                          "repo_admin_change_team_access": False})


def _ensure_repository(api: gitea.Gitea, org: str, repo: str, spec: dict) -> None:
    existing = api.get(f"repos/{org}/{repo}")
    if existing is None:
        existing = api.post(f"orgs/{org}/repos", {"name": repo, "private": True, "default_branch": "main", "auto_init": False})
    api.patch(f"repos/{org}/{repo}", {
        "has_actions": bool(spec.get("actions")), "has_wiki": False, "has_projects": False, "has_packages": False,
        "has_releases": True, "allow_merge_commits": True, "allow_squash_merge": True, "allow_rebase": False,
        "allow_rebase_explicit": False, "default_delete_branch_after_merge": True,
    })
    if existing.get("empty", False) and spec.get("source"):
        publish(org, repo, paths.WORKSPACE / spec["source"])


def publish(org: str, repo: str, directory: Path, message: str = "Initial import from the workspace",
            user: str = gitea.ADMIN_USER, branch: str = "main", on_top_of: str | None = None,
            keep_from_base: tuple[str, ...] = ()) -> bool:
    """Pushes the directory's current content (honouring its .gitignore) as one commit.

    With `on_top_of`, the commit's parent is that remote branch, so the push is an
    ordinary fast-forward by `user` and goes through the branch protection like any
    other push. Returns False when there was nothing to commit.

    Uses a temporary Git directory with the workspace as its work tree, so no .git
    directory is ever created in the workspace. Credentials and the CA travel through
    environment-based Git config, never on the command line.
    """
    url = f"https://localhost:3000/{org}/{repo}.git"
    with tempfile.TemporaryDirectory(prefix="sscp-publish-") as git_dir:
        env = _git_env(user, git_dir, directory)
        git = ["git", "-c", "init.defaultBranch=main"]
        shell.run([*git, "init", "-q"], env=env)
        if on_top_of:
            shell.run([*git, "fetch", "-q", url, on_top_of], env=env, timeout=300)
            shell.run([*git, "reset", "-q", "--soft", "FETCH_HEAD"], env=env)
        shell.run([*git, "add", "-A"], env=env, timeout=300)
        if on_top_of and keep_from_base:
            # Files another identity owns on the base branch (for example the digests the
            # release bot writes) are taken from there, so a proposal never reverts them.
            shell.run([*git, "restore", "--source=FETCH_HEAD", "--staged", "--", *keep_from_base], env=env)
        if on_top_of and shell.run([*git, "diff", "--cached", "--quiet", "HEAD"], env=env, check=False).returncode == 0:
            return False
        shell.run([*git, "commit", "-q", "-m", message], env=env)
        shell.run([*git, "push", "-q", url, f"HEAD:refs/heads/{branch}"], env=env, timeout=300)
        return True



def push_tag(tag: str, commit: str | None = None, user: str = "rhea") -> str:
    """Pushes a tag on commerce-app as `user` (by default the release manager).

    The tag is pushed with Git like a person would, so Gitea's tag protection decides
    whether `user` may create it, and the push webhook starts the release. Tags on the
    commit at the tip of main unless `commit` is given. Returns the tagged commit.
    """
    url = "https://localhost:3000/commerce/commerce-app.git"
    with tempfile.TemporaryDirectory(prefix="sscp-tag-") as git_dir:
        env = _git_env(user, git_dir)
        shell.run(["git", "init", "-q", "--bare", git_dir], env=env)
        shell.run(["git", "fetch", "-q", url, "main"], env=env, timeout=300)
        target = commit or shell.run(["git", "rev-parse", "FETCH_HEAD"], env=env).stdout.strip()
        shell.run(["git", "tag", "-a", tag, target, "-m", f"Release {tag}"], env=env)
        shell.run(["git", "push", "-q", url, f"refs/tags/{tag}"], env=env, timeout=300)
    return target


def _git_env(user: str, git_dir: str, work_tree: Path | None = None) -> dict[str, str]:
    """Git environment for `user`: credentials and the CA travel through environment-based
    Git config, never on the command line."""
    password = gitea.admin_password() if user == gitea.ADMIN_USER else user_password(user)
    auth = base64.b64encode(f"{user}:{password}".encode()).decode()
    env = {
        "GIT_DIR": git_dir,
        "GIT_AUTHOR_NAME": user, "GIT_AUTHOR_EMAIL": f"{user}@sscp.test",
        "GIT_COMMITTER_NAME": user, "GIT_COMMITTER_EMAIL": f"{user}@sscp.test",
        "GIT_CONFIG_COUNT": "4",
        "GIT_CONFIG_KEY_0": "http.extraHeader", "GIT_CONFIG_VALUE_0": f"Authorization: Basic {auth}",
        "GIT_CONFIG_KEY_1": "http.sslCAInfo", "GIT_CONFIG_VALUE_1": str(pki.CA_CERT.resolve()),
        "GIT_CONFIG_KEY_2": "core.autocrlf", "GIT_CONFIG_VALUE_2": "false",
        # Git for Windows defaults to schannel, which ignores sslCAInfo.
        "GIT_CONFIG_KEY_3": "http.sslBackend", "GIT_CONFIG_VALUE_3": "openssl",
    }
    if work_tree is not None:
        env["GIT_WORK_TREE"] = str(work_tree)
    return env


# Repositories people change through pull requests: Gitea name, workspace directory and
# the files on main that only the release bot writes.
PROPOSABLE = {
    "commerce-app": ("commerce/commerce-app", paths.COMMERCE_APP_REPO, ()),
    "commerce-gitops": ("platform/commerce-gitops", paths.GITOPS_REPO,
                        ("overlays/local/kustomization.yaml", "overlays/local/release.yaml")),
}


def propose(branch: str, title: str, user: str = "alice", repository: str = "commerce-app") -> int:
    """Pushes a workspace repository as a branch and opens a pull request as `user`.

    This is how a person's change reaches a protected repository: main accepts changes
    only through a pull request with the required checks and a review. Returns the pull
    request number (an existing open one for the branch is reused).
    """
    full_name, directory, release_managed = PROPOSABLE[repository]
    org, repo = full_name.split("/")
    publish(org, repo, directory, title, user=user, branch=branch, on_top_of="main", keep_from_base=release_managed)
    api = gitea.Gitea()
    open_pulls = api.get(f"repos/{full_name}/pulls", params={"state": "open"}) or []
    existing = next((p for p in open_pulls if p["head"]["ref"] == branch), None)
    if existing:
        return existing["number"]
    created = api.post(f"repos/{full_name}/pulls", {"head": branch, "base": "main", "title": title}, sudo=user)
    return created["number"]


def approve(repository: str, number: int, user: str) -> None:
    """Approves a pull request as `user`. Gitea counts the approval only if `user` is in
    the branch's approver teams."""
    full_name = PROPOSABLE[repository][0]
    gitea.Gitea().post(f"repos/{full_name}/pulls/{number}/reviews", {"event": "APPROVED", "body": "Reviewed."}, sudo=user)


def merge(repository: str, number: int, user: str) -> str:
    """Merges a pull request as `user`; Gitea refuses it unless the branch protection is
    satisfied (approvals and required statuses). Returns the new commit on main."""
    full_name = PROPOSABLE[repository][0]
    api = gitea.Gitea()
    response = api.request("POST", f"repos/{full_name}/pulls/{number}/merge", json={"Do": "merge"}, sudo=user, expected=(405, 409))
    if response.status_code != 200:
        raise RuntimeError(f"Gitea refused the merge ({response.status_code}): {response.text[:300]}")
    return api.get(f"repos/{full_name}/pulls/{number}")["merge_commit_sha"]


def announce_main(repository: str = "commerce-app") -> str:
    """Asks Gitea to send the push event for the tip of main again. The Control Plane
    handles it like any push: it builds the commit unless a build for it already exists.

    Needed after a fresh start, where the repository's first commit is imported before
    the Control Plane is running. Returns the announced commit.
    """
    full_name = PROPOSABLE[repository][0]
    api = gitea.Gitea()
    hook = next(h for h in api.get(f"repos/{full_name}/hooks") or [] if h.get("config", {}).get("url") == WEBHOOK_URL)
    # Gitea copies `ref` into the payload as given, so it must be the full ref name.
    api.request("POST", f"repos/{full_name}/hooks/{hook['id']}/tests", params={"ref": "refs/heads/main"})
    return api.get(f"repos/{full_name}/branches/main")["commit"]["id"]


def sync_platform(message: str) -> bool:
    """Publishes workspace changes of the platform repository as a commit by the platform engineer."""
    return publish("platform", "supply-chain-platform", paths.PLATFORM_REPO, message, user="pat", on_top_of="main")


def _units(spec: dict) -> dict[str, str]:
    if "units" in spec:
        return dict(spec["units"])
    permission = spec.get("permission", "read")
    return {**{unit: permission for unit in PEOPLE_UNITS}, "repo.actions": "read"}


def _ensure_team(api: gitea.Gitea, org: str, team: str, spec: dict) -> None:
    payload = {
        "name": team, "permission": spec.get("permission", "read"), "units_map": _units(spec),
        "includes_all_repositories": False, "can_create_org_repo": False, "description": spec.get("description", ""),
    }
    teams = api.get(f"orgs/{org}/teams") or []
    existing = next((t for t in teams if t["name"] == team), None)
    if existing is None:
        existing = api.post(f"orgs/{org}/teams", payload)
    else:
        api.patch(f"teams/{existing['id']}", payload)
    for member in spec.get("members", []):
        api.put(f"teams/{existing['id']}/members/{member}")
    for repo in spec.get("repositories", []):
        api.put(f"teams/{existing['id']}/repos/{org}/{repo}")


def _ensure_protections(api: gitea.Gitea, org: str, repo: str, spec: dict) -> None:
    for branch, rule in spec.get("protectedBranches", {}).items():
        payload = {
            "enable_push": bool(rule.get("push", False)),
            "enable_push_whitelist": bool(rule.get("pushTeams")),
            "push_whitelist_teams": rule.get("pushTeams", []),
            "enable_force_push": False,
            "required_approvals": rule.get("requiredApprovals", 0),
            "enable_approvals_whitelist": bool(rule.get("approvers")),
            "approvals_whitelist_teams": rule.get("approvers", []),
            "dismiss_stale_approvals": bool(rule.get("dismissStaleApprovals", False)),
            "block_on_rejected_reviews": bool(rule.get("blockOnRejectedReviews", False)),
            "block_admin_merge_override": bool(rule.get("blockAdminMergeOverride", False)),
            "enable_status_check": bool(rule.get("statusChecks")),
            "status_check_contexts": rule.get("statusChecks", []),
        }
        if api.get(f"repos/{org}/{repo}/branch_protections/{branch}") is None:
            api.post(f"repos/{org}/{repo}/branch_protections", {"rule_name": branch, **payload})
        else:
            api.patch(f"repos/{org}/{repo}/branch_protections/{branch}", payload)

    existing = {t["name_pattern"]: t for t in api.get(f"repos/{org}/{repo}/tag_protections") or []}
    for pattern, rule in spec.get("protectedTags", {}).items():
        payload = {"name_pattern": pattern, "whitelist_teams": rule.get("teams", []), "whitelist_usernames": rule.get("users", [])}
        if pattern in existing:
            api.patch(f"repos/{org}/{repo}/tag_protections/{existing[pattern]['id']}", payload)
        else:
            api.post(f"repos/{org}/{repo}/tag_protections", payload)


def _ensure_webhook(api: gitea.Gitea, org: str, repo: str) -> None:
    payload = {
        "type": "gitea", "active": True, "events": WEBHOOK_EVENTS, "branch_filter": "*",
        # Gitea signs each delivery with HMAC-SHA256 of the body using this secret.
        "config": {"url": WEBHOOK_URL, "content_type": "json", "secret": webhook_secret()},
    }
    hooks = api.get(f"repos/{org}/{repo}/hooks") or []
    existing = next((h for h in hooks if h.get("config", {}).get("url") == WEBHOOK_URL), None)
    if existing is None:
        api.post(f"repos/{org}/{repo}/hooks", payload)
    else:
        api.patch(f"repos/{org}/{repo}/hooks/{existing['id']}", {k: v for k, v in payload.items() if k != "type"})


def user_token(name: str, scopes: list[str]) -> str:
    """Returns a working API token for a Gitea user, creating a new one when needed."""
    key = f"gitea.token.{name}"
    stored = credentials.find(key)
    # 401 means the token no longer exists; 403 only means it lacks the read:user scope.
    if stored and gitea.Gitea(token=stored).request("GET", "user", expected=(401, 403)).status_code != 401:
        return stored
    api = gitea.Gitea(username=name, password=user_password(name))
    token_name = "sscp-bootstrap"
    api.request("DELETE", f"users/{name}/tokens/{token_name}", expected=(404, 422))
    token = api.post(f"users/{name}/tokens", {"name": token_name, "scopes": scopes})["sha1"]
    credentials.put(key, token)
    return token


def controlplane_token() -> str:
    return user_token("sscp-controlplane", ["write:repository", "read:user"])


def source_reader_token() -> str:
    return user_token("sscp-source-reader", ["read:repository"])
