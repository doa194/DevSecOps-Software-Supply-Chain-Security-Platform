# Configuration reference

Where every piece of configuration lives, what it controls and who reads it. The platform
follows three rules:

1. **Versions in one place.** Every image, chart and tool version is pinned in
   `supply-chain-platform/versions.yaml`; no other file hard-codes its own.
2. **Structure in Git, secrets generated.** Files in the repositories describe structure
   (users, roles, policies, manifests). Passwords, tokens and keys are generated on the
   workstation at first start and stored in Vault or under `.local/`, never committed.
3. **Generated output is disposable.** Everything under `.local/generated/` is recreated
   by `sscp up`.

## Configuration files in the repositories

| File | Controls | Read by |
|---|---|---|
| `supply-chain-platform/versions.yaml` | every pinned version: images (by digest), Helm charts, kind, the kind node image, the CI tool mirror list | `sscp` (Compose env, kind, Helm, runner config, tool mirror), `sscp doctor` |
| `supply-chain-platform/policy/trust-policy.yaml` | mandatory evidence, severity gates, Checkov downgrades, vulnerability SLA and database age, exception rules | Security Control Plane (built into its image) |
| `supply-chain-platform/policy/applications.yaml` | governed applications: source repository, deployables, candidate and trusted Harbor repositories, GitOps repository and environment | Security Control Plane (built into its image) |
| `supply-chain-platform/platform/compose.yaml` | the software factory: services, networks, fixed addresses, memory limits, health checks | Docker Compose via `sscp` |
| `supply-chain-platform/platform/gitea/source-control.yaml` | Gitea people and machine accounts, organisations, teams, repositories, branch and tag protections, webhooks | `sscp up` |
| `supply-chain-platform/platform/keycloak/realms/*.yaml` | the `platform` and `commerce` realms: roles, clients, personas, token lifetimes | `sscp up` |
| `supply-chain-platform/platform/harbor/harbor.yml.template` | Harbor's input configuration | Harbor's `prepare` generator, run by `sscp` |
| `supply-chain-platform/platform/vault/config/vault.hcl` | Vault's listener, storage (integrated Raft) and telemetry | Vault |
| `supply-chain-platform/platform/postgres/init/` | creates one database and owner per platform service on first start | PostgreSQL |
| `supply-chain-platform/platform/runner/docker-run` | how each CI runner starts its private Docker daemon and waits for it | runner containers |
| `supply-chain-platform/platform/controlplane/Dockerfile` | the Control Plane image | `sscp up` |
| `supply-chain-platform/.gitea/workflows/*.yaml` | the four platform pipelines | Gitea Actions (dispatched by the Control Plane) |
| `supply-chain-platform/ci/rules/` | the platform's Gitleaks configuration and Semgrep rules | the CI helper, inside `ci-tools` |
| `supply-chain-platform/cluster/kind.yaml` | the kind cluster: node, published ports, etcd settings | `sscp up --with cluster` |
| `supply-chain-platform/cluster/helm/*.yaml` | Helm values for Argo CD, Kyverno, External Secrets and the Trivy Operator | `sscp up --with cluster` |
| `supply-chain-platform/cluster/bootstrap/platform.yaml` | the `platform` Argo CD project and the `platform-cluster` application | `sscp up --with cluster`, once |
| `supply-chain-platform/cluster/platform/` | namespaces, service accounts, network policies, secret stores, admission policies, the `commerce` Argo CD project and application, the observability stack | Argo CD (`platform-cluster` application) |
| `commerce-app/.gitea/workflows/validation.yaml` | the application's own build-and-test workflow | Gitea Actions (validation runner) |
| `commerce-app/config/publishers.yaml` | which service may publish which integration events | the workload |
| `commerce-app/nuget.config`, `Directory.Packages.props`, `packages.lock.json` | package sources, source mapping, signature validation, central versions, locked dependency graph | .NET restore |
| `commerce-gitops/base/`, `commerce-gitops/overlays/local/` | the workload's desired Kubernetes state; the overlay pins released digests | Argo CD (`commerce` application) |

## `versions.yaml`

```yaml
schemaVersion: 1
tools:        # kind (URL + SHA-256), expected kubectl/Helm/Python/.NET versions, SonarScanner
kubernetes:   # the kind node image, by digest
charts:       # argocd, kyverno, externalSecrets, trivyOperator: repository, chart, version
images:       # every container image, "name:tag@sha256:digest"
ciMirror:     # the images copied into Harbor's platform-tools project for the pipelines
```

`sscp doctor` enforces two rules on this file: every image carries a digest, and every
downloaded tool carries a SHA-256 (the checker itself is unit-tested in
`automation/tests/test_pins.py`). The tag in an image reference is informational; the
digest is what is pulled.

**Updating a version:** change it in `versions.yaml`, run `uv run sscp up` with the
affected capabilities (images are re-mirrored and services restarted as needed), then run
the relevant test and verification suites.

## Generated files (`.local/`)

`.local/` sits at the workspace root, is git-ignored, and is deleted by `sscp reset`.

| Path | Contents | Sensitivity |
|---|---|---|
| `.local/pki/ca.crt`, `ca.key` | the platform's root certificate authority | `ca.key` is secret |
| `.local/pki/<service>/` | TLS certificate and key per service (Gitea, Harbor, Vault, Keycloak, MinIO, Control Plane, commerce) | keys are secret |
| `.local/secrets/bootstrap.json` | every generated password and token (see below) | secret |
| `.local/secrets/vault-init.json` | Vault's unseal keys (3 shares, threshold 2) | **most sensitive file on the workstation** |
| `.local/secrets/crane/` | registry credentials for the automation's `crane` runs | secret |
| `.local/generated/compose.env` | the environment file Compose reads: image references from `versions.yaml` and service credentials | secret |
| `.local/generated/runners/<zone>/config.yaml` | each runner's configuration, including its zone's AppRole `secret_id` | secret |
| `.local/generated/harbor/` | Harbor's generated deployment | contains Harbor's internal secrets |
| `.local/generated/kubeconfig` | cluster-admin access to kind | secret |
| `.local/generated/cosign-commerce.pub` | the release key's **public** half | public |
| `.local/generated/tool-images.json` | each mirrored tool: upstream pin and mirrored reference | public |
| `.local/generated/ci-tools-build/`, `sonar-dotnet-build/`, `grype-db/` | build contexts and downloads for platform-built images | public |
| `.local/generated/minio-controlplane-policy.json` | the Control Plane's MinIO permissions (put, get, list; no delete) | public |

`.runs/` (workspace root) holds logs of host-run processes, for example
`.runs/workload-dev/`. `.tools/bin/` holds the downloaded, checksum-verified kind binary.

### Credential store keys

`.local/secrets/bootstrap.json` maps names to generated values. Read one with:

```bash
uv run python -c "from sscp import credentials; print(credentials.find('<key>'))"
```

| Key prefix | Holds |
|---|---|
| `gitea.admin`, `gitea.user.<name>`, `gitea.token.*`, `gitea.metrics-token` | Gitea administrator and persona passwords; machine account tokens; Prometheus' token |
| `harbor.admin`, `harbor.robot.*` | Harbor administrator and robot secrets |
| `vault.bootstrap-token`, `vault.approle.*` | Vault's scoped bootstrap token; the CI zones' AppRole `secret_id`s |
| `keycloak.bootstrap-admin`, `keycloak.platform.*`, `keycloak.commerce.*` | Keycloak administrator; client secrets and persona passwords per realm |
| `postgres.*` | owner and runtime passwords for the platform databases |
| `minio.*` | MinIO root user and the Control Plane's evidence user |
| `sonarqube.*` | SonarQube administrator, analysis user and token |
| `controlplane.webhook-secret`, `argocd.notifications-token` | the Gitea webhook HMAC secret; Argo CD's deployment-report token |
| `cluster.*` | workload secrets delivered to the cluster through Vault: database roles, broker, cache, object storage, event-signing keys |
| `grafana.admin` | Grafana administrator |
| `workload-dev.*` | secrets of the host development environment |

The store exists so that re-running `sscp up` writes the same values again. Running
components never read it: they get their secrets from Vault, Compose or Kubernetes.

## Vault layout

| Path | Contents | Readable by |
|---|---|---|
| `kv/ci/<zone>/*` (`security`, `build`, `trust`) | each zone's Control Plane client, source token, registry robot, SonarQube token, test personas | that zone's AppRole, only from its runner's address |
| `kv/ci/trust-signer/*` | promoter robot and GitOps bot token | the `trust-signer` role, i.e. only with a signing grant |
| `kv/workload/commerce/*`, `kv/workload/commerce-data/*` | workload runtime secrets | External Secrets in that namespace |
| `kv/platform/cluster/*` | Argo CD tokens, the cluster pull robot, observability secrets | External Secrets in the matching namespace |
| `transit/keys/cosign-commerce` | the release signing key (ECDSA P-256, not exportable) | signing only through `trust-signer` |
| `auth/approle/*` | CI zone roles, `trust-signer`, the Control Plane's grant-issuing role | — |
| `auth/jwt-kind` | the cluster's service-account key; roles `eso-<namespace>` | — |

Details: [secret-management.md](secret-management.md), [release-signing.md](release-signing.md).

## Security Control Plane settings

Set as environment variables in `platform/compose.yaml` (secrets come from
`.local/generated/compose.env`); defaults in `src/Sscp.ControlPlane.Api/appsettings.json`.

| Setting | Value | Purpose |
|---|---|---|
| `ConnectionStrings__controlplane` | `Host=postgres.sscp.test;Database=controlplane;Username=controlplane_app;…` | the restricted runtime role |
| `Identity__Issuer` / `Identity__Audience` | `https://keycloak.sscp.test:9443/realms/platform` / `controlplane-api` | token validation |
| `EvidenceStore__Endpoint`, `__Bucket`, `__AccessKey`, `__SecretKey` | `https://minio.sscp.test:9000`, `evidence`, `controlplane`, generated | write-once evidence storage |
| `Gitea__BaseUrl`, `__Token`, `__WebhookSecret` | `https://gitea.sscp.test:3000`, the `sscp-controlplane` token, the HMAC secret | dispatch, statuses, webhook verification |
| `Vault__Address`, `__RoleId`, `__SecretId` | `https://vault.sscp.test:8200`, the grant-issuing AppRole | signing grants |
| `ArgoCd__NotificationToken` | generated | authenticates Argo CD's deployment reports |
| `Policy__TrustPolicyPath`, `__ApplicationsPath` | `/policy/*.yaml` inside the image | the trust policy |
| `Kestrel__Endpoints__Api__Url` / `Management__Url` | `https://+:8443` / `http://+:9464` | API and internal management listeners |
| `*__CaCertificatePath` | `/etc/sscp/certs/sscp-root-ca.crt` | trust only the platform's own CA for outgoing TLS |

## Host ports

| Port | Service |
|---|---|
| 3000 | Gitea (HTTPS) |
| 7443 | Security Control Plane API (HTTPS) |
| 8088 | Commerce gateway (kind NodePort 30080) |
| 8200 | Vault (HTTPS) |
| 8443 | Harbor (HTTPS) |
| 8444 | Argo CD (HTTPS, kind NodePort 30444) |
| 3300 | Grafana (kind NodePort 30300) |
| 9443 | Keycloak (HTTPS) |
| 9990 | Prometheus (kind NodePort 30990) |
| 9100 | SonarQube (HTTP) |
| 5433, 6379, 5672, 15672, 9010 | development data services (`--with workload` only): PostgreSQL, Redis, RabbitMQ, RabbitMQ management, MinIO |

All are bound to `127.0.0.1`. Internal addresses and networks are listed in
[architecture-overview.md](architecture-overview.md#networks-and-fixed-addresses).

## Settings tuned for a workstation

A few settings exist only so the platform runs reliably on one machine, including one
whose Docker data lives on a hard disk (PostgreSQL's commit durability, etcd's disk sync,
Grafana's in-memory database, longer health-check timeouts, Vault's memory limit). Each is
listed with its production alternative in
[production-considerations.md](production-considerations.md#settings-for-a-slow-workstation-disk).
