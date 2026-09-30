"""Trust zone: re-evaluate a release, then promote, sign and attest its images and hand
them to GitOps.

The trust zone has no standing right to sign, to push trusted images or to change the
GitOps repository. For an approved release the Control Plane hands this run a signing
grant: the role_id of Vault's `trust-signer` role and a response-wrapped, single-use
secret_id. Unwrapping it (which also proves nobody else did) and logging in yields a
short-lived Vault token that can use the non-exportable `cosign-commerce` Transit key and
read the promoter and GitOps credentials. The private key never leaves Vault: Cosign sends
digests to Vault's Transit engine and gets signatures back.

For every image of the release this module
  1. copies the candidate by digest into the trusted project (plus the release tag),
  2. signs the trusted digest and attaches three signed attestations: SLSA-style build
     provenance, the trust decision made for this release, and the SBOM,
  3. verifies the trusted image with the release key's public half, as admission control
     does, before reporting it to the Control Plane.
Finally it commits the new digests to the GitOps repository, which Argo CD deploys.

Signatures and attestations are Sigstore bundles stored as OCI referrers of the image.
They are not uploaded to a public transparency log: the platform is fully local (see
docs/release-signing.md for what that means for verification).
"""
from __future__ import annotations

import base64
import json
import os
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

import yaml

from sscp_ci import controlplane, http, zone

KEY_NAME = "cosign-commerce"
KEY_REFERENCE = f"hashivault://{KEY_NAME}"

# Cosign 3 defaults to Sigstore's public services (Rekor, Fulcio, signing config). These
# flags keep signing offline; the signature is a Sigstore bundle attached to the image as
# an OCI referrer.
OFFLINE_SIGNING = ["--tlog-upload=false", "--use-signing-config=false", "--new-bundle-format=true", "--yes"]
OFFLINE_VERIFY = ["--insecure-ignore-tlog=true", "--new-bundle-format=true"]

SIGNATURE_TYPE = "https://sigstore.dev/cosign/sign/v1"
ATTESTATIONS = {
    "provenance": "https://slsa.dev/provenance/v1",
    "trustDecision": "https://sscp.test/attestations/trust-decision/v1",
    "sbom": "https://cyclonedx.org/bom",
}

GITOPS_OVERLAY = "overlays/local"


class ReleaseError(RuntimeError):
    pass


@dataclass
class Signer:
    """What a redeemed signing grant gives this job, for a few minutes."""
    vault_token: str
    tool_env: dict[str, str]        # Cosign and crane: Vault token and registry login
    registry_auth: str              # promoter robot, HTTP basic credentials
    gitops: dict[str, str]          # GitOps bot token and repository
    public_key: Path


def evaluate(release_id: str) -> int:
    """Final trust decision right before signing. Exit status 1 when the release is rejected."""
    decision = controlplane.request("POST", f"/api/releases/{release_id}/evaluation", json_body={"runId": zone.run_id()}).json()
    release = decision["release"]
    print(f"release {release['tag']} ({release['commit'][:12]}): {decision['outcome']} -> {release['state']}")
    for item in decision["artifacts"]:
        controlplane.print_decision(item["decision"], f"  {item['deployable']}@{item['digest']}")
    return 1 if decision["outcome"] == "FAIL" else 0


def publish(release_id: str, workdir: Path) -> int:
    plan = controlplane.request("GET", f"/api/releases/{release_id}/plan").json()
    if plan["state"] not in ("Approved", "ApprovedWithException"):
        raise ReleaseError(f"release {plan['tag']} is {plan['state']}; only approved releases are signed")

    workdir.mkdir(parents=True, exist_ok=True)
    token = _redeem_grant(release_id)
    try:
        with tempfile.TemporaryDirectory(prefix="registry-auth-") as docker_config:
            signer = _signer(token, Path(docker_config), workdir)
            trusted = {artifact["deployable"]: _release_artifact(release_id, plan, artifact, signer, workdir)
                       for artifact in plan["artifacts"]}
            commit = _update_gitops(plan, trusted, signer)
    finally:
        # The token would expire within minutes anyway; revoking it closes the window now.
        http.request("POST", f"{zone.env('VAULT_ADDR')}/v1/auth/token/revoke-self", headers={"X-Vault-Token": token}, ok=(403,))

    controlplane.request("POST", f"/api/releases/{release_id}/gitops", json_body={"runId": zone.run_id(), "commit": commit})
    print(f"release {plan['tag']}: {len(trusted)} images promoted, signed and attested; GitOps commit {commit[:12]}")
    return 0


def fail(release_id: str, reason: str) -> int:
    response = controlplane.request("POST", f"/api/releases/{release_id}/failure",
                                    json_body={"runId": zone.run_id(), "reason": reason[:500]}, ok=(409,))
    print(f"release {release_id} marked failed: {reason}" if response.status == 204 else f"release {release_id} already final")
    return 0


# ------------------------------------------------------------------ signing grant

def _redeem_grant(release_id: str) -> str:
    grant = controlplane.request("POST", f"/api/releases/{release_id}/signing-grant", json_body={"runId": zone.run_id()}).json()
    vault = zone.env("VAULT_ADDR")
    # Unwrapping works exactly once. If it fails, someone else already opened the grant:
    # stop instead of asking for another one.
    unwrapped = http.request("POST", f"{vault}/v1/sys/wrapping/unwrap", headers={"X-Vault-Token": grant["wrappedSecretId"]}, ok=(400, 403))
    if unwrapped.status != 200:
        raise ReleaseError("the signing grant could not be unwrapped: it was already used or has expired")
    secret_id = unwrapped.json()["data"]["secret_id"]
    login = http.request("POST", f"{vault}/v1/auth/approle/login", json_body={"role_id": grant["roleId"], "secret_id": secret_id})
    print(f"signing grant redeemed (valid until {grant['expiresAt']})")
    return login.json()["auth"]["client_token"]


def _vault_read(path: str, token: str) -> dict:
    return http.request("GET", f"{zone.env('VAULT_ADDR')}/v1/{path}", headers={"X-Vault-Token": token}).json()["data"]


def _signer(token: str, docker_config: Path, workdir: Path) -> Signer:
    promoter = _vault_read("kv/data/ci/trust-signer/harbor", token)["data"]
    auth = base64.b64encode(f"{promoter['username']}:{promoter['password']}".encode()).decode()
    (docker_config / "config.json").write_text(json.dumps({"auths": {promoter["registry"]: {"auth": auth}}}), encoding="utf-8")
    env = {
        "PATH": os.environ["PATH"], "HOME": str(docker_config), "DOCKER_CONFIG": str(docker_config),
        "VAULT_ADDR": zone.env("VAULT_ADDR"), "VAULT_TOKEN": token,
    }
    # The public half, to verify exactly as admission control does: offline, with the
    # key file, without asking Vault.
    key = _vault_read(f"transit/keys/{KEY_NAME}", token)
    public_key = workdir / f"{KEY_NAME}.pub"
    public_key.write_text(key["keys"][str(key["latest_version"])]["public_key"], encoding="utf-8")
    return Signer(token, env, auth, _vault_read("kv/data/ci/trust-signer/gitops", token)["data"], public_key)


# ------------------------------------------------------------------ per image

def _release_artifact(release_id: str, plan: dict, artifact: dict, signer: Signer, workdir: Path) -> str:
    """Promotes, signs, attests and verifies one image; returns its trusted reference."""
    deployable, digest = artifact["deployable"], artifact["digest"]
    trusted_repository = f"{plan['trustedRepository']}/{deployable}"
    trusted = f"{trusted_repository}@{digest}"
    material = controlplane.request("GET", f"/api/releases/{release_id}/artifacts/{artifact['id']}/attestations?runId={zone.run_id()}").json()

    # Same manifest, same digest: what was scanned is what gets signed and deployed.
    _run(["crane", "copy", artifact["reference"], f"{trusted_repository}:{plan['tag']}"], signer.tool_env)
    print(f"{deployable}: promoted to {trusted}")
    _run(["cosign", "sign", "--key", KEY_REFERENCE, *OFFLINE_SIGNING, trusted], signer.tool_env)
    for name, predicate_type in ATTESTATIONS.items():
        predicate = workdir / f"{deployable}.{name}.json"
        predicate.write_text(json.dumps(material[name]["predicate"]), encoding="utf-8")
        _run(["cosign", "attest", "--key", KEY_REFERENCE, *OFFLINE_SIGNING, "--type", predicate_type, "--predicate", str(predicate), trusted],
             signer.tool_env)

    _run(["cosign", "verify", "--key", str(signer.public_key), *OFFLINE_VERIFY, trusted], signer.tool_env, quiet=True)
    for predicate_type in ATTESTATIONS.values():
        _run(["cosign", "verify-attestation", "--key", str(signer.public_key), *OFFLINE_VERIFY, "--type", predicate_type, trusted],
             signer.tool_env, quiet=True)
    print(f"{deployable}: signature and {len(ATTESTATIONS)} attestations verified")

    referrers = _referrers(trusted_repository, digest, signer.registry_auth)
    signature = next((d for d, kind in referrers if kind == SIGNATURE_TYPE), None)
    if signature is None:
        raise ReleaseError(f"{deployable}: the signature bundle is not attached to {trusted}")
    controlplane.request("POST", f"/api/releases/{release_id}/signatures", json_body={
        "runId": zone.run_id(), "artifactId": artifact["id"], "keyReference": KEY_REFERENCE,
        "signatureReference": f"{trusted_repository}@{signature}",
        "attestationReferences": [f"{trusted_repository}@{d}#{kind}" for d, kind in referrers if kind in ATTESTATIONS.values()],
    })
    controlplane.request("POST", f"/api/releases/{release_id}/promotions", json_body={
        "runId": zone.run_id(), "artifactId": artifact["id"], "trustedRepository": trusted_repository,
    })
    return trusted


def _referrers(repository: str, digest: str, basic_auth: str) -> list[tuple[str, str]]:
    """(referrer digest, predicate type) of the Sigstore bundles attached to an image,
    read through the registry's OCI 1.1 referrers API."""
    host, _, path = repository.partition("/")
    token = http.request("GET", f"https://{host}/service/token?service=harbor-registry&scope=repository:{path}:pull",
                         headers={"Authorization": f"Basic {basic_auth}"}).json()["token"]
    index = http.request("GET", f"https://{host}/v2/{path}/referrers/{digest}",
                         headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.oci.image.index.v1+json"}).json()
    return [(m["digest"], (m.get("annotations") or {}).get("dev.sigstore.bundle.predicateType", ""))
            for m in index.get("manifests", [])]


# ------------------------------------------------------------------ GitOps

def _update_gitops(plan: dict, trusted: dict[str, str], signer: Signer) -> str:
    """Commits the release's digests to the GitOps repository in one commit, as the
    GitOps bot. Argo CD notices the commit and deploys it; nothing here touches the cluster."""
    api = f"{zone.env('SSCP_GITEA_URL')}/api/v1/repos/{signer.gitops['repository']}"
    headers = {"Authorization": f"token {signer.gitops['token']}"}
    path = f"{GITOPS_OVERLAY}/kustomization.yaml"
    current = http.request("GET", f"{api}/contents/{path}?ref=main", headers=headers).json()
    kustomization = yaml.safe_load(base64.b64decode(current["content"]))
    kustomization["images"] = [
        {"name": deployable, "newName": reference.split("@")[0], "digest": reference.split("@")[1]}
        for deployable, reference in sorted(trusted.items())
    ]
    release = {
        "apiVersion": "v1", "kind": "ConfigMap",
        "metadata": {"name": "commerce-release", "namespace": "commerce", "labels": {"app.kubernetes.io/part-of": "commerce"}},
        "data": {"tag": plan["tag"], "commit": plan["commit"], "release": plan["id"]},
    }
    header = "# Written by the release pipeline (trust zone). Digests only; see docs/gitops-and-admission.md.\n"
    body = {
        "branch": "main",
        "message": f"Release {plan['tag']} of {plan['application']} ({plan['commit'][:12]})",
        "files": [
            {"operation": "update", "path": path, "sha": current["sha"],
             "content": base64.b64encode((header + yaml.safe_dump(kustomization, sort_keys=False)).encode()).decode()},
            _file_change(api, headers, f"{GITOPS_OVERLAY}/release.yaml", header + yaml.safe_dump(release, sort_keys=False)),
        ],
    }
    result = http.request("POST", f"{api}/contents", headers=headers, json_body=body).json()
    return result["commit"]["sha"]


def _file_change(api: str, headers: dict[str, str], path: str, text: str) -> dict:
    existing = http.request("GET", f"{api}/contents/{path}?ref=main", headers=headers, ok=(404,))
    change = {"path": path, "content": base64.b64encode(text.encode()).decode()}
    if existing.status == 404:
        return {"operation": "create", **change}
    return {"operation": "update", "sha": existing.json()["sha"], **change}


def _run(command: list[str], env: dict[str, str], quiet: bool = False) -> None:
    result = subprocess.run(command, env=env, capture_output=True, text=True, timeout=600)
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()[-1500:]
        raise ReleaseError(f"{command[0]} {command[1]} failed ({result.returncode}): {detail}")
    if not quiet and result.stderr.strip():
        print("\n".join(f"    {line}" for line in result.stderr.strip().splitlines()[-2:]), file=sys.stderr)
