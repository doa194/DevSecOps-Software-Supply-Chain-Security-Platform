"""Runs the commerce workload on the host for development (`sscp workload ...`).

The six deployables run as local processes against the `workload` Compose profile
(PostgreSQL, Redis, RabbitMQ, MinIO) and the platform Keycloak. This is the fast inner
loop for developing the workload; the cluster deployment uses the same settings but gets
them from Vault through External Secrets.

What `setup` prepares:
- one PostgreSQL role per module/worker plus a schema-owner role for migrations,
- one ECDSA signing key per publisher (commerce-api, document-worker),
- the commerce Keycloak realm.
"""
from __future__ import annotations

import json
import os
import signal
import subprocess
from dataclasses import dataclass, field
from pathlib import Path

import yaml
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

from sscp import compose, console, credentials, docker, paths, pki, shell
from sscp.services import keycloak

RUN_DIR = paths.RUNS_DIR / "workload-dev"
PUBLISHERS_FILE = paths.COMMERCE_APP_REPO / "config" / "publishers.yaml"
KEYCLOAK_ISSUER = "https://keycloak.sscp.test:9443/realms/commerce"

API_SCHEMAS = ["identity", "customers", "catalog", "inventory", "orders", "payments", "documents", "administration"]
WORKER_ROLES = {
    "audit": "commerce_audit_worker",
    "reporting": "commerce_reporting_worker",
    "notifications": "commerce_notification_worker",
    "document_processing": "commerce_document_worker",
}


@dataclass(frozen=True)
class Service:
    name: str
    project: str
    port: int
    schemas: list[str] = field(default_factory=list)
    publisher: bool = False
    storage: bool = False
    redis: bool = False
    migrates: bool = False

    @property
    def dll(self) -> Path:
        project = paths.COMMERCE_APP_REPO / self.project
        return project / "bin" / "Release" / "net10.0" / f"{project.name}.dll"


SERVICES = [
    Service("commerce-api", "src/Hosts/Commerce.Api", 5100, API_SCHEMAS, publisher=True, storage=True, redis=True, migrates=True),
    Service("audit-worker", "src/Workers/Commerce.Workers.Audit", 5101, ["audit"], migrates=True),
    Service("reporting-worker", "src/Workers/Commerce.Workers.Reporting", 5102, ["reporting"], migrates=True),
    Service("notification-worker", "src/Workers/Commerce.Workers.Notification", 5103, ["notifications"], migrates=True),
    Service("document-worker", "src/Workers/Commerce.Workers.Documents", 5104, ["document_processing"], publisher=True, storage=True, migrates=True),
    Service("commerce-gateway", "src/Hosts/Commerce.Gateway", 5080),
]


def role_for(schema: str) -> str:
    return WORKER_ROLES.get(schema, f"commerce_{schema}")


def role_password(role: str) -> str:
    return credentials.get(f"workload-dev.db.{role}")


def connection_string(role: str) -> str:
    return f"Host=localhost;Port=5433;Database=commerce;Username={role};Password={role_password(role)}"


def signing_key(publisher: str) -> str:
    """PEM private key of a publisher, generated once."""
    path = paths.SECRETS_DIR / "workload" / "signing" / f"{publisher}.pem"
    if not path.exists():
        key = ec.generate_private_key(ec.SECP256R1())
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    return path.read_text(encoding="ascii")


def public_key(publisher: str) -> str:
    private = serialization.load_pem_private_key(signing_key(publisher).encode("ascii"), password=None)
    return private.public_key().public_bytes(serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo).decode("ascii")


def ensure_database_roles() -> None:
    """Creates the schema-owner role and one runtime role per schema (idempotent)."""
    roles = ["commerce_owner", *[role_for(s) for s in API_SCHEMAS], *WORKER_ROLES.values()]
    statements = []
    for role in roles:
        password = role_password(role)
        statements.append(
            f"DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') "
            f"THEN CREATE ROLE {role} LOGIN PASSWORD '{password}'; ELSE ALTER ROLE {role} PASSWORD '{password}'; END IF; END $$;"
        )
        statements.append(f"GRANT CONNECT ON DATABASE commerce TO {role};")
    statements += [
        "GRANT CREATE ON DATABASE commerce TO commerce_owner;",
        # Nobody but the owner may create objects in the public schema.
        "REVOKE CREATE ON SCHEMA public FROM PUBLIC;",
    ]
    container = docker.compose_container(compose.PROJECT, "commerce-postgres")
    if container is None:
        raise RuntimeError("the workload PostgreSQL is not running; run `sscp up --with workload` first")
    docker.exec_in(container, ["psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres", "-d", "commerce"], input_text="\n".join(statements))


def environment(service: Service) -> dict[str, str]:
    publishers = yaml.safe_load(PUBLISHERS_FILE.read_text(encoding="utf-8"))["publishers"]
    env = {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ASPNETCORE_URLS": f"http://localhost:{service.port}",
        "Identity__Issuer": KEYCLOAK_ISSUER,
        # From the host, Keycloak is reached on localhost; the token issuer stays the same.
        "Identity__MetadataAddress": "https://localhost:9443/realms/commerce/.well-known/openid-configuration",
        "Identity__CaCertificatePath": str(pki.CA_CERT.resolve()),
        "Messaging__ConnectionString": f"amqp://commerce:{credentials.get('workload-dev.rabbitmq')}@localhost:5672/commerce",
        "Messaging__Source": service.name,
        "OpenApi__Enabled": "true",
    }
    for index, (publisher, spec) in enumerate(publishers.items()):
        prefix = f"Messaging__TrustedPublishers__{publisher}-key"
        env[f"{prefix}__Source"] = publisher
        env[f"{prefix}__PublicKeyPem"] = public_key(publisher)
        for event_index, event in enumerate(spec["events"]):
            env[f"{prefix}__AllowedTypes__{event_index}"] = event
        _ = index
    if service.publisher:
        env["Messaging__Signing__KeyId"] = f"{service.name}-key"
        env["Messaging__Signing__PrivateKeyPem"] = signing_key(service.name)
    for schema in service.schemas:
        env[f"ConnectionStrings__{schema}"] = connection_string(role_for(schema))
    if service.migrates:
        env["ConnectionStrings__migrations"] = connection_string("commerce_owner")
    if service.redis:
        env["ConnectionStrings__redis"] = f"localhost:6379,password={credentials.get('workload-dev.redis')},abortConnect=false"
    if service.storage:
        env |= {
            "ObjectStorage__Endpoint": "http://localhost:9010",
            "ObjectStorage__AccessKey": "commerce-admin",
            "ObjectStorage__SecretKey": credentials.get("workload-dev.minio"),
        }
    if service.name == "commerce-api":
        env |= {
            "Identity__Admin__BaseUrl": "https://localhost:9443",
            "Identity__Admin__ClientSecret": keycloak.client_secret("commerce", "commerce-identity-admin"),
            "Identity__Admin__CaCertificatePath": str(pki.CA_CERT.resolve()),
        }
    if service.name == "commerce-gateway":
        for cluster, port in {"api": 5100, "audit-worker": 5101, "reporting-worker": 5102, "notification-worker": 5103}.items():
            env[f"ReverseProxy__Clusters__{cluster}__Destinations__primary__Address"] = f"http://localhost:{port}"
    return env


def setup() -> int:
    console.heading("Preparing the workload development environment")
    with console.step("apply the commerce Keycloak realm"):
        keycloak.wait_ready()
        keycloak.apply_realm("commerce")
    with console.step("create database roles (one per module and worker, plus the schema owner)"):
        ensure_database_roles()
    with console.step("generate publisher signing keys"):
        for publisher in ("commerce-api", "document-worker"):
            signing_key(publisher)
    with console.step("build the commerce solution"):
        shell.run(["dotnet", "build", "Commerce.slnx", "-c", "Release", "-nologo", "-v", "q"], cwd=paths.COMMERCE_APP_REPO, timeout=900)
    for service in SERVICES:
        if service.migrates:
            with console.step(f"migrate {service.name}"):
                shell.run(["dotnet", str(service.dll), "migrate"], env=environment(service), cwd=service.dll.parent, timeout=300)
    return 0


def reset() -> int:
    """Deletes all workload development data: database, broker messages and cache."""
    stop()
    console.heading("Resetting workload development data")
    with console.step("recreate the commerce database"):
        postgres = docker.compose_container(compose.PROJECT, "commerce-postgres")
        docker.exec_in(postgres, ["psql", "-U", "postgres", "-d", "postgres", "-c", "DROP DATABASE IF EXISTS commerce WITH (FORCE);", "-c", "CREATE DATABASE commerce;"])
    with console.step("recreate the RabbitMQ virtual host"):
        rabbit = docker.compose_container(compose.PROJECT, "rabbitmq")
        docker.exec_in(rabbit, ["sh", "-c", 'rabbitmqctl -q delete_vhost commerce; rabbitmqctl -q add_vhost commerce && rabbitmqctl -q set_permissions -p commerce commerce ".*" ".*" ".*"'])
    with console.step("flush Redis"):
        redis = docker.compose_container(compose.PROJECT, "redis")
        docker.exec_in(redis, ["sh", "-c", 'redis-cli -a "$REDIS_PASSWORD" --no-auth-warning flushall'])
    return setup()


def start() -> int:
    RUN_DIR.mkdir(parents=True, exist_ok=True)
    pids = {}
    for service in SERVICES:
        log = (RUN_DIR / f"{service.name}.log").open("w", encoding="utf-8")
        process = subprocess.Popen(
            ["dotnet", str(service.dll)], env={**os.environ, **environment(service)}, cwd=service.dll.parent,
            stdout=log, stderr=subprocess.STDOUT,
            creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
        )
        pids[service.name] = process.pid
        console.ok(f"{service.name} started on http://localhost:{service.port} (log: {log.name})")
    (RUN_DIR / "pids.json").write_text(json.dumps(pids), encoding="utf-8")
    return 0


def stop() -> int:
    pid_file = RUN_DIR / "pids.json"
    if not pid_file.exists():
        return 0
    for name, pid in json.loads(pid_file.read_text(encoding="utf-8")).items():
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, check=False)
        else:
            try:
                os.kill(pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        console.ok(f"{name} stopped")
    pid_file.unlink()
    return 0
