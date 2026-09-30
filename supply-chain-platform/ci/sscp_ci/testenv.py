"""Ephemeral security-test environment for dynamic testing, inside the security zone.

Starts the candidate images of one build (by digest) with their data services on a
private network in the zone's own Docker daemon, applies migrations, waits until the
gateway answers, and removes everything afterwards. Credentials are generated per run
and never leave the job. The services validate real tokens from the platform's
Keycloak `commerce` realm, so authorization is tested exactly as it is deployed.
"""
from __future__ import annotations

import os
import secrets
import subprocess
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path

import yaml

from sscp_ci import tools

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
    schemas: tuple[str, ...] = ()
    publisher: bool = False
    storage: bool = False
    redis: bool = False
    migrates: bool = False


SERVICES = [
    Service("commerce-api", tuple(API_SCHEMAS), publisher=True, storage=True, redis=True, migrates=True),
    Service("audit-worker", ("audit",), migrates=True),
    Service("reporting-worker", ("reporting",), migrates=True),
    Service("notification-worker", ("notifications",), migrates=True),
    Service("document-worker", ("document_processing",), publisher=True, storage=True, migrates=True),
    Service("commerce-gateway"),
]

# Hardening applied to every workload container, as in the cluster.
HARDENING = ["--read-only", "--tmpfs", "/tmp", "--cap-drop", "ALL", "--security-opt", "no-new-privileges"]


class EnvironmentError(RuntimeError):
    pass


def _docker(*args: str, check: bool = True, input_text: str | None = None, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(["docker", *args], capture_output=True, text=True, input=input_text,
                            env={**os.environ, **env} if env else None)
    if check and result.returncode != 0:
        raise EnvironmentError(f"docker {' '.join(args[:3])} failed: {result.stderr.strip()[-600:]}")
    return result


def role_for(schema: str) -> str:
    return WORKER_ROLES.get(schema, f"commerce_{schema}")


@dataclass
class TestEnvironment:
    images: dict[str, str]                  # deployable -> candidate reference by digest
    publishers_file: Path                   # config/publishers.yaml of the commit under test
    identity_admin_secret: str
    run: str = field(default_factory=lambda: uuid.uuid4().hex[:8])
    passwords: dict[str, str] = field(default_factory=dict)
    keys: dict[str, tuple[str, str]] = field(default_factory=dict)   # publisher -> (private PEM, public PEM)

    @property
    def network(self) -> str:
        return f"sscp-test-{self.run}"

    @property
    def config_volume(self) -> str:
        return f"sscp-test-{self.run}-config"

    def container(self, name: str) -> str:
        return f"sscp-test-{self.run}-{name}"

    def password(self, name: str) -> str:
        return self.passwords.setdefault(name, secrets.token_hex(16))

    # ------------------------------------------------------------ lifecycle

    def start(self) -> None:
        _docker("network", "create", self.network)
        self._config_volume()
        self._data_services()
        self._database_roles()
        self._signing_keys()
        for service in SERVICES:
            if service.migrates:
                self._migrate(service)
        for service in SERVICES:
            self._run(service)
        self._wait_for_gateway()

    def join(self, container_id: str) -> None:
        """Connects the job container itself, so tests can call the gateway by name."""
        _docker("network", "connect", self.network, container_id)

    def stop(self, job_container: str | None = None) -> None:
        if job_container:
            _docker("network", "disconnect", "-f", self.network, job_container, check=False)
        names = _docker("ps", "-aq", "--filter", f"name=sscp-test-{self.run}-", check=False).stdout.split()
        if names:
            _docker("rm", "-f", *names, check=False)
        _docker("network", "rm", self.network, check=False)
        _docker("volume", "rm", "-f", self.config_volume, check=False)

    def logs(self, service: str, lines: int = 80) -> str:
        return _docker("logs", "--tail", str(lines), self.container(service), check=False).stdout

    # ------------------------------------------------------------ steps

    def _config_volume(self) -> None:
        """Volume holding the platform CA, mounted read-only into the services."""
        _docker("volume", "create", self.config_volume)
        loader = self.container("config-loader")
        _docker("create", "--name", loader, "--network", "none", "-v", f"{self.config_volume}:/config", tools.image("alpine"),
                "sh", "-c", "chmod -R a+rX /config")
        _docker("cp", "/usr/local/share/ca-certificates/sscp-root-ca.crt", f"{loader}:/config/ca.crt")
        _docker("start", "-a", loader)
        _docker("rm", loader)

    def _data_services(self) -> None:
        common = ["run", "-d", "--network", self.network]
        _docker(*common, "--name", self.container("postgres"), "--network-alias", "postgres",
                "-e", f"POSTGRES_PASSWORD={self.password('postgres')}", "-e", "POSTGRES_DB=commerce", tools.image("postgres"))
        _docker(*common, "--name", self.container("redis"), "--network-alias", "redis", tools.image("redis"),
                "redis-server", "--requirepass", self.password("redis"))
        _docker(*common, "--name", self.container("rabbitmq"), "--network-alias", "rabbitmq",
                "-e", "RABBITMQ_DEFAULT_USER=commerce", "-e", f"RABBITMQ_DEFAULT_PASS={self.password('rabbitmq')}",
                "-e", "RABBITMQ_DEFAULT_VHOST=commerce", tools.image("rabbitmq"))
        _docker(*common, "--name", self.container("minio"), "--network-alias", "minio",
                "-e", "MINIO_ROOT_USER=commerce-admin", "-e", f"MINIO_ROOT_PASSWORD={self.password('minio')}",
                tools.image("minio"), "server", "/data", "--address", ":9000")
        self._wait("postgres", ["pg_isready", "-U", "postgres", "-d", "commerce"])
        self._wait("rabbitmq", ["rabbitmq-diagnostics", "-q", "check_port_connectivity"], timeout=180)
        self._wait_http("http://minio:9000/minio/health/ready")

    def _wait(self, service: str, command: list[str], timeout: float = 120) -> None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if _docker("exec", self.container(service), *command, check=False).returncode == 0:
                return
            time.sleep(2)
        raise EnvironmentError(f"{service} did not become ready: {self.logs(service, 30)}")

    def _wait_http(self, url: str, timeout: float = 180) -> None:
        """Polls a URL from a throw-away container on the environment's network."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            probe = _docker("run", "--rm", "--network", self.network, tools.image("alpine"),
                            "wget", "-q", "-O", "/dev/null", "-T", "3", url, check=False)
            if probe.returncode == 0:
                return
            time.sleep(3)
        raise EnvironmentError(f"{url} did not answer within {timeout:.0f}s")

    def _database_roles(self) -> None:
        roles = ["commerce_owner", *[role_for(s) for s in API_SCHEMAS], *WORKER_ROLES.values()]
        statements = [f"CREATE ROLE {role} LOGIN PASSWORD '{self.password(role)}'; GRANT CONNECT ON DATABASE commerce TO {role};" for role in roles]
        statements += ["GRANT CREATE ON DATABASE commerce TO commerce_owner;", "REVOKE CREATE ON SCHEMA public FROM PUBLIC;"]
        _docker("exec", "-i", self.container("postgres"), "psql", "-v", "ON_ERROR_STOP=1", "-q", "-U", "postgres", "-d", "commerce",
                input_text="\n".join(statements))

    def _signing_keys(self) -> None:
        """One ECDSA P-256 key per publisher, generated for this run only."""
        for publisher in (s.name for s in SERVICES if s.publisher):
            private = subprocess.run(["openssl", "genpkey", "-algorithm", "EC", "-pkeyopt", "ec_paramgen_curve:P-256"],
                                     capture_output=True, text=True, check=True).stdout
            public = subprocess.run(["openssl", "pkey", "-pubout"], input=private, capture_output=True, text=True, check=True).stdout
            self.keys[publisher] = (private, public)

    def _connection(self, role: str) -> str:
        return f"Host=postgres;Database=commerce;Username={role};Password={self.password(role)}"

    def environment(self, service: Service) -> dict[str, str]:
        publishers = yaml.safe_load(self.publishers_file.read_text(encoding="utf-8"))["publishers"]
        env = {
            "ASPNETCORE_ENVIRONMENT": "Production",
            "Identity__Issuer": KEYCLOAK_ISSUER,
            "Identity__CaCertificatePath": "/etc/sscp/ca.crt",
            "Messaging__ConnectionString": f"amqp://commerce:{self.password('rabbitmq')}@rabbitmq:5672/commerce",
            "Messaging__Source": service.name,
            # The API description is published only in this test environment, for the scanner.
            "OpenApi__Enabled": "true",
        }
        for publisher, spec in publishers.items():
            prefix = f"Messaging__TrustedPublishers__{publisher}-key"
            env[f"{prefix}__Source"] = publisher
            env[f"{prefix}__PublicKeyPem"] = self.keys[publisher][1]
            for index, event in enumerate(spec["events"]):
                env[f"{prefix}__AllowedTypes__{index}"] = event
        if service.publisher:
            env["Messaging__Signing__KeyId"] = f"{service.name}-key"
            env["Messaging__Signing__PrivateKeyPem"] = self.keys[service.name][0]
        for schema in service.schemas:
            env[f"ConnectionStrings__{schema}"] = self._connection(role_for(schema))
        if service.redis:
            env["ConnectionStrings__redis"] = f"redis:6379,password={self.password('redis')},abortConnect=false"
        if service.storage:
            env |= {"ObjectStorage__Endpoint": "http://minio:9000", "ObjectStorage__AccessKey": "commerce-admin",
                    "ObjectStorage__SecretKey": self.password("minio")}
        if service.name == "commerce-api":
            env |= {"Identity__Admin__BaseUrl": "https://keycloak.sscp.test:9443", "Identity__Admin__ClientSecret": self.identity_admin_secret,
                    "Identity__Admin__CaCertificatePath": "/etc/sscp/ca.crt"}
        return env

    @staticmethod
    def _pass_through(env: dict[str, str]) -> list[str]:
        """`-e NAME` copies each value from the docker CLI's own environment: no secrets on
        the command line, and multi-line values (PEM keys) arrive intact."""
        return [argument for name in env for argument in ("-e", name)]

    def _migrate(self, service: Service) -> None:
        env = self.environment(service) | {"ConnectionStrings__migrations": self._connection("commerce_owner")}
        result = _docker("run", "--rm", "--network", self.network, *HARDENING, "-v", f"{self.config_volume}:/etc/sscp:ro",
                         *self._pass_through(env), self.images[service.name], "migrate", check=False, env=env)
        if result.returncode != 0:
            raise EnvironmentError(f"migrating {service.name} failed: {(result.stdout + result.stderr)[-800:]}")

    def _run(self, service: Service) -> None:
        env = self.environment(service)
        _docker("run", "-d", "--name", self.container(service.name), "--network", self.network, "--network-alias", service.name,
                *HARDENING, "-v", f"{self.config_volume}:/etc/sscp:ro", *self._pass_through(env), self.images[service.name], env=env)

    def _wait_for_gateway(self) -> None:
        for service in ("commerce-api", "audit-worker", "reporting-worker", "notification-worker", "document-worker", "commerce-gateway"):
            try:
                self._wait_http(f"http://{service}:8080/health/ready", timeout=180)
            except EnvironmentError as error:
                raise EnvironmentError(f"{error}\n--- {service} log ---\n{self.logs(service)}") from None
