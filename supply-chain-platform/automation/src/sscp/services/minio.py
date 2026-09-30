"""MinIO evidence store: bucket with object lock and a least-privilege Control Plane user.

Raw scanner reports are evidence. The bucket is created with object locking and a
default GOVERNANCE retention, so an uploaded report cannot be overwritten or deleted
during the retention period by the Control Plane's own credentials. Only the MinIO root
user (kept in .local/secrets, never given to a service) could bypass it.
"""
from __future__ import annotations

import json
import time

from sscp import credentials, paths, pki, shell, versions

EVIDENCE_BUCKET = "evidence"
CONTROLPLANE_USER = "controlplane"
RETENTION_DAYS = 30
POLICY_FILE = paths.GENERATED_DIR / "minio-controlplane-policy.json"

# Write-once evidence: the Control Plane may add and read reports, never delete them.
POLICY = {
    "Version": "2012-10-17",
    "Statement": [
        {
            "Effect": "Allow",
            "Action": ["s3:PutObject", "s3:GetObject", "s3:GetObjectRetention", "s3:GetObjectVersion"],
            "Resource": [f"arn:aws:s3:::{EVIDENCE_BUCKET}/*"],
        },
        {"Effect": "Allow", "Action": ["s3:ListBucket", "s3:GetBucketLocation"], "Resource": [f"arn:aws:s3:::{EVIDENCE_BUCKET}"]},
    ],
}


def root_user() -> str:
    return credentials.get("minio.root-user", lambda: "sscp-minio-admin")


def controlplane_secret() -> str:
    return credentials.get("minio.controlplane")


def _mc(*args: str, check: bool = True) -> str:
    """Runs one mc command in a throw-away container on the data network.

    The Chainguard image has no shell, so every command is its own container.
    """
    mounts = [
        f"type=bind,source={pki.CA_CERT.resolve().as_posix()},target=/tmp/mc/certs/CAs/sscp-root-ca.crt,readonly",
        f"type=bind,source={POLICY_FILE.resolve().as_posix()},target=/tmp/policy.json,readonly",
    ]
    # Root inside this short-lived admin container only: Docker creates the parent
    # directories of the CA mount as root, and mc must write its config next to them.
    command = ["docker", "run", "--rm", "--user", "0:0", "--network", "sscp-data"]
    for mount in mounts:
        command += ["--mount", mount]
    command += [
        "-e", f"MC_HOST_sscp=https://{root_user()}:{credentials.get('minio.root')}@minio.sscp.test:9000",
        versions.image("minioClient"), "--config-dir", "/tmp/mc", *args,
    ]
    return shell.run(command, check=check, timeout=120).stdout


def wait_ready(timeout: float = 120) -> None:
    deadline = time.monotonic() + timeout
    while _mc_ready() is False:
        if time.monotonic() > deadline:
            raise TimeoutError("MinIO did not become ready")
        time.sleep(3)


def _mc_ready() -> bool:
    POLICY_FILE.parent.mkdir(parents=True, exist_ok=True)
    POLICY_FILE.write_text(json.dumps(POLICY), encoding="utf-8")
    return "is ready" in _mc("ready", "sscp", check=False)


def configure() -> None:
    POLICY_FILE.write_text(json.dumps(POLICY), encoding="utf-8")
    _mc("mb", "--ignore-existing", "--with-lock", f"sscp/{EVIDENCE_BUCKET}")
    _mc("retention", "set", "--default", "GOVERNANCE", f"{RETENTION_DAYS}d", f"sscp/{EVIDENCE_BUCKET}")
    _mc("admin", "policy", "create", "sscp", "controlplane-evidence", "/tmp/policy.json")
    _mc("admin", "user", "add", "sscp", CONTROLPLANE_USER, controlplane_secret())
    # Attaching an already attached policy reports an error; the end state is what matters.
    _mc("admin", "policy", "attach", "sscp", "controlplane-evidence", "--user", CONTROLPLANE_USER, check=False)


def default_retention() -> str:
    return _mc("retention", "info", "--default", f"sscp/{EVIDENCE_BUCKET}")
