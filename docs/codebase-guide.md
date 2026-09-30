# Codebase guide

A map of the workspace for someone opening it for the first time: what each directory
contains, what it is responsible for and how it connects to the rest. Every important
source file also starts with a short comment explaining why it exists.

## The workspace

```text
<workspace>/
├── README.md                  project introduction (start here)
├── docs/                      this documentation
├── supply-chain-platform/     platform repository  → Gitea platform/supply-chain-platform
├── commerce-app/              application repository → Gitea commerce/commerce-app
├── commerce-gitops/           GitOps repository     → Gitea platform/commerce-gitops
├── .local/                    generated on first start: certificates, credentials, configuration (git-ignored)
├── .runs/                     logs of processes run on the host (git-ignored)
└── .tools/bin/                downloaded, checksum-verified tools such as kind (git-ignored)
```

The three repository directories sit side by side on disk; `sscp` publishes each one as
its own Gitea repository. The workspace root itself (`README.md`, `docs/`) is not
published to Gitea.

## `supply-chain-platform/` — the platform repository

Owned by the platform team. It contains everything that decides whether software can be
trusted, plus the automation that builds the whole environment.

```text
supply-chain-platform/
├── src/                         Security Control Plane (.NET 10)
│   ├── Sscp.ControlPlane.Domain/          rules with no dependencies
│   ├── Sscp.ControlPlane.Application/     use cases, report readers, zone permissions
│   ├── Sscp.ControlPlane.Infrastructure/  PostgreSQL, MinIO, Vault, Gitea adapters
│   └── Sscp.ControlPlane.Api/             HTTP host, authentication, background jobs
├── tests/                       Control Plane tests: unit, architecture, integration, component
├── policy/                      trust-policy.yaml, applications.yaml (built into the Control Plane)
├── .gitea/workflows/            the four platform pipelines (pr, main, release, deployment)
├── ci/                          the CI helper and what CI jobs run
│   ├── sscp_ci/                 `sscp-ci`: scanners, builds, dynamic tests, release signing
│   ├── rules/                   platform-owned Gitleaks configuration and Semgrep rules
│   ├── sonar/                   the sonar-dotnet analysis image
│   ├── tests/                   unit tests of the CI helper
│   └── Dockerfile               the ci-tools job image
├── automation/                  `sscp`: bootstrap, verification, repository operations
│   ├── src/sscp/                the command and one module per concern
│   └── tests/                   unit tests and the operational verification suites
├── platform/                    the software factory definition
│   ├── compose.yaml             Docker Compose services
│   ├── gitea/                   people, teams, repositories, protections
│   ├── keycloak/realms/         the platform and commerce realms
│   ├── harbor/                  Harbor's generator input
│   ├── vault/                   Vault server configuration
│   ├── postgres/                database initialisation
│   ├── controlplane/            the Control Plane Dockerfile
│   └── runner/                  runner start script
├── cluster/                     the deployment target definition
│   ├── kind.yaml                the kind cluster
│   ├── helm/                    values for Argo CD, Kyverno, External Secrets, Trivy Operator
│   ├── bootstrap/               the Argo CD project and application that manage cluster/platform
│   └── platform/                cluster security state synced by Argo CD
│       ├── policies/            Kyverno and native admission policies
│       ├── observability/       Prometheus, Loki, Grafana, OTel Collector, alerts, dashboards
│       └── *.yaml               namespaces, service accounts, network policies, secret stores, Argo CD project
├── versions.yaml                every pinned version
├── pyproject.toml, uv.lock      the Python automation and its locked dependencies
└── SupplyChainPlatform.slnx     the .NET solution
```

### Security Control Plane (`src/`)

Clean-architecture layering, enforced by `tests/Sscp.ControlPlane.ArchitectureTests`:

| Project | Responsibility | Key files |
|---|---|---|
| `Domain` | State machines and pure rules: artifacts, builds, releases, risk exceptions, the `TrustEvaluator`, the audit hash chain. No I/O. | `Policy/TrustEvaluator.cs`, `Artifacts/Artifact.cs`, `Releases/Release.cs`, `Exceptions/RiskException.cs`, `Auditing/AuditChain.cs` |
| `Application` | Use cases and ports: evidence ingestion, trust evaluation, releases, signing grants, deployments, exceptions, pipeline orchestration; scanner report readers; which CI zone may do what; metrics | `Evidence/EvidenceService.cs`, `Evidence/Readers/*`, `Trust/TrustService.cs`, `Releases/*`, `Orchestration/OrchestrationService.cs`, `CallerZones.cs`, `Ports.cs` |
| `Infrastructure` | Adapters: EF Core store and migrations, MinIO evidence store, YAML policy loading, Gitea (dispatch, statuses, webhook parsing, desired state), Vault grant issuer, read-only queries | `Persistence/*`, `Storage/S3EvidenceStore.cs`, `Gitea/*`, `Vault/VaultSigningGrantIssuer.cs`, `Queries/ControlPlaneQueries.cs` |
| `Api` | HTTP endpoints, token validation and zone mapping, background jobs (exception expiry, state gauges, run watcher), health and metrics | `Program.cs`, `Endpoints/*`, `Security/ControlPlaneAuth.cs`, `Background/BackgroundJobs.cs`, `Hosting/Management.cs` |

Explained in [security-control-plane.md](security-control-plane.md).

### CI helper (`ci/sscp_ci/`)

| Module | Responsibility |
|---|---|
| `__main__.py` | the `sscp-ci` command and its sub-commands |
| `zone.py` | the job's zone identity: Vault AppRole login, zone secrets, Control Plane token (renewed before expiry) |
| `controlplane.py` | calls to the Control Plane (always with the zone token and the bound run id) |
| `tools.py` | runs tools as sibling containers: copy of the source in a private volume, no network, capabilities dropped |
| `scanners.py` | Gitleaks, Semgrep, Checkov and Hadolint invocations; deletes repository-supplied scanner configuration |
| `dockerfile_policy.py` | the base-image policy for Dockerfiles |
| `artifacts.py` | image builds, push, registration, SBOM, Trivy and Grype scans |
| `dynamic.py`, `testenv.py`, `authz_suite.py` | the throw-away security-test environment, the authorization suite, the ZAP scan |
| `release.py` | release evaluation, signing grant redemption, promotion, signing, attestation, verification, the GitOps commit |
| `http.py` | minimal HTTPS client that trusts only the platform CA |

Explained in [ci-pipelines.md](ci-pipelines.md) and [security-scanning.md](security-scanning.md).

### Automation (`automation/src/sscp/`)

| Module | Responsibility |
|---|---|
| `cli.py` | the `sscp` command-line interface |
| `lifecycle.py` | `up`, `down`, `reset`, `status` |
| `doctor.py`, `pins.py` | workstation checks and the pinning policy |
| `paths.py`, `versions.py`, `credentials.py`, `pki.py` | workspace locations, pinned versions, the credential store, the local CA |
| `docker.py`, `compose.py`, `shell.py`, `http.py`, `console.py` | Docker networks and containers, Compose, subprocesses, HTTP, terminal output |
| `services/` | one module per service: Vault, Gitea, Harbor, Keycloak, MinIO, SonarQube, Control Plane |
| `sourcecontrol.py` | Gitea desired state, publishing repositories, pull requests, tags |
| `toolmirror.py`, `vulndb.py`, `registry.py` | the tool mirror, vulnerability databases, Harbor projects and robots |
| `cizones.py`, `signing.py` | CI zone identities and runners; release signing identities in Vault |
| `cluster.py` | the kind cluster, add-ons, Vault access for External Secrets, platform state |
| `probe.py` | runs small probes inside platform networks for the operational tests |
| `workload.py` | runs the workload on the host for development |
| `verify.py` | the `sscp verify` suites |

Explained in [cli-reference.md](cli-reference.md).

## `commerce-app/` — the application repository

Owned by the commerce team. Treated as untrusted input by the platform: nothing here can
influence how it is scanned, built, judged or signed.

```text
commerce-app/
├── src/
│   ├── BuildingBlocks/
│   │   ├── Commerce.SharedKernel/     domain primitives, integration event base types, audit contract
│   │   └── Commerce.BuildingBlocks/   outbox/inbox, signed messaging, security, persistence, telemetry, web
│   ├── Modules/<Module>/              one project per business module plus its Contracts project
│   │                                  (Identity, Customers, Catalog, Inventory, Orders, Payments, Documents, Administration)
│   ├── Hosts/
│   │   ├── Commerce.Api/              the modular monolith host
│   │   └── Commerce.Gateway/          the YARP gateway, the only public entry point
│   └── Workers/                       Audit, Documents, Notification, Reporting
├── tests/
│   ├── Commerce.UnitTests/            domain rules, policies, message signing
│   ├── Commerce.ArchitectureTests/    module and layer boundaries checked on compiled code
│   ├── Commerce.IntegrationTests/     real PostgreSQL, RabbitMQ, Redis, MinIO, Keycloak (Testcontainers)
│   ├── Commerce.ComponentTests/       the API over HTTP with real infrastructure
│   └── Commerce.Gateway.ComponentTests/ the gateway's boundary rules
├── config/publishers.yaml             which service may publish which integration events
├── Dockerfile                         one build target per deployable
├── .gitea/workflows/validation.yaml   the application's own build-and-test workflow
├── Directory.Build.props              shared build settings (warnings as errors, NuGet audit, lock files)
├── Directory.Packages.props           central package versions
└── nuget.config                       nuget.org only, source mapping, signature validation
```

Explained in [workload-architecture.md](workload-architecture.md),
[identity-and-authorization.md](identity-and-authorization.md) and
[messaging-and-data.md](messaging-and-data.md).

## `commerce-gitops/` — the GitOps repository

The desired Kubernetes state of the workload. Argo CD applies it; the release bot changes
only the overlay.

```text
commerce-gitops/
├── base/
│   ├── commerce/          the six Deployments and Services, config, ExternalSecrets,
│   │                      messaging settings, registry access, the migrations Job
│   └── commerce-data/     PostgreSQL, Redis, RabbitMQ, MinIO (mirrored images, pinned by digest)
└── overlays/local/
    ├── kustomization.yaml pins each deployable to commerce-trusted/<name>@sha256:… (written by releases)
    └── release.yaml       the commerce-release ConfigMap: tag, commit, release id (written by releases)
```

Explained in [gitops-and-admission.md](gitops-and-admission.md).

## Where to change what

| I want to… | Change | Then |
|---|---|---|
| change what the trust policy requires | `supply-chain-platform/policy/trust-policy.yaml` | `sscp repo sync`, `sscp up` (rebuilds the Control Plane) |
| add or tighten a Semgrep rule | `supply-chain-platform/ci/rules/semgrep/` (with its test file) | `sscp repo sync`, `sscp up --with ci` (rebuilds `ci-tools`) |
| change a pipeline | `supply-chain-platform/.gitea/workflows/` or `ci/sscp_ci/` | `sscp repo sync`, `sscp up --with ci` |
| add an admission policy | `supply-chain-platform/cluster/platform/policies/` and `kustomization.yaml` | `sscp repo sync` (Argo CD applies it) |
| update a pinned version | `supply-chain-platform/versions.yaml` | `sscp up` with the affected capabilities |
| change the workload | `commerce-app/` | `sscp repo propose`, review, merge, release |
| change the workload's Kubernetes configuration | `commerce-gitops/base/` | `sscp repo propose --repo commerce-gitops`, review, merge |
| add a person or team | `supply-chain-platform/platform/gitea/source-control.yaml` or the realm files | `sscp up` |
