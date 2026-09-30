# Production considerations

The platform runs on one workstation. Apart from pulling pinned images and vulnerability
data, it works offline. Its security controls are real and enforced, for example:

- admission control refuses unsigned images;
- Vault binds CI identities to runner addresses;
- the Control Plane refuses evidence from the wrong pipeline run.

Some surrounding choices, however, are made for a single machine. This document separates
three things for each area:

- **Now**: what is implemented and verified.
- **Local simplification**: what is deliberately simpler because it runs on a workstation.
- **For production**: what would have to change. **Nothing in this column is
  implemented.**

| Area | Most important production change |
|---|---|
| [Hosting and isolation](#hosting-and-isolation) | separate hosts per trust zone; ephemeral runners |
| [Source control and review](#source-control-and-review) | enforced second-person review of the platform repository; MFA |
| [CI pipelines and scanning](#ci-pipelines-and-scanning) | scheduled vulnerability-data refresh; commercial or community rule feeds reviewed on a schedule |
| [Secrets and Vault](#secrets-and-vault) | HA Vault with auto-unseal; secrets as files; dynamic credentials |
| [Identity](#identity) | organisation identity provider, authorization-code flow with PKCE |
| [Signing, provenance and the registry](#signing-provenance-and-the-registry) | transparency log and timestamping; key rotation procedure |
| [Kubernetes](#kubernetes) | multi-node cluster; break-glass-only cluster admin |
| [Network and TLS](#network-and-tls) | organisation PKI; encryption between all services |
| [Observability](#observability) | alert routing; monitoring outside the monitored systems |
| [Audit and evidence](#audit-and-evidence) | COMPLIANCE-mode retention; chain head anchored externally |
| [Backup and disaster recovery](#backup-and-disaster-recovery) | backups of every stateful service, restore drills |
| [Settings for a slow workstation disk](#settings-for-a-slow-workstation-disk) | revert every setting in that table |

## Hosting and isolation

| Now | Local simplification | For production |
|---|---|---|
| Every service, the four CI zones and the kind cluster are containers in one Docker Desktop VM; zones are separated by their own rootless Docker daemons, networks, Vault identities and Harbor robots ([trust-boundaries.md](trust-boundaries.md)) | Control of the Docker VM is control of everything; the runners are privileged containers so that they can start their nested daemons | Separate hosts or VMs per trust zone (at least the trust zone on its own), ephemeral runners per job, and no shared hypervisor administrator between the factory and the zones |
| One kind node | No scheduling isolation between workloads; a node compromise exposes every namespace | Several nodes, with the data services and the platform add-ons on separate node pools |
| Fixed addresses on `sscp-edge` are the zone identity boundary (Vault CIDR binding) | Addresses are assigned by Docker Compose on the same host | Workload identity from the runner platform (per-job OIDC or SPIFFE), which Gitea Actions does not offer ([design-decisions.md](design-decisions.md)) |

## Source control and review

| Now | Local simplification | For production |
|---|---|---|
| Branch protection, required statuses, required approvals, a single release manager allowed to push tags, a GitOps bot that may only write the overlay | All personas are accounts created by the bootstrap, with passwords in `.local/secrets/bootstrap.json` | Accounts from the organisation's identity provider, with MFA; signed commits and tags verified at the gate |
| Changes to the platform repository (security pipelines, cluster policies) are pushed by the platform engineer (`sscp repo sync`) | The platform repository has no review rule, so one engineer can change the security pipelines | Required review by a second platform engineer for the platform repository, including `cluster/platform/` |

## CI pipelines and scanning

| Now | Local simplification | For production |
|---|---|---|
| Four CI zones on long-lived runners, each with a private rootless Docker daemon; jobs run in fresh containers | Runners and their daemons persist between jobs; the security zone keeps scanner database caches | Ephemeral runners created per job and destroyed afterwards, so nothing persists between jobs |
| Vulnerability databases mirrored into Harbor; evidence older than seven days is refused | Refreshed by `sscp up --with ci` or `sscp tools refresh-db`, run by a person | A scheduled refresh job, with an alert before the seven-day limit is reached |
| Semgrep and Gitleaks rules are platform-owned files in the platform repository | The rule sets are small and hand-written for this workload ([security-scanning.md](security-scanning.md)) | Maintained rule packs, reviewed and updated on a schedule, with rule tests |
| SonarQube Community Build analyses `main` | No pull-request analysis (not supported by the Community Build) | An edition with pull-request analysis, or another SAST tool with the same gate |
| Dynamic tests run in a throw-away environment on the security runner | ZAP scans as one persona for at most 10 minutes of active scanning | Longer, scheduled scans per role against a staging environment, in addition to the per-build gate |

## Secrets and Vault

| Now | Local simplification | For production |
|---|---|---|
| Vault holds every runtime secret, the signing key (non-exportable Transit key) and the CI zone identities; its audit device records every request | One Vault node with integrated storage; the unseal keys and the initial root token are in `.local/secrets/vault-init.json`, and `sscp up` unseals automatically | A highly available Vault cluster with auto-unseal from an HSM or KMS; unseal and recovery keys split among people and kept offline |
| Workload secrets reach the cluster through External Secrets with a per-namespace Vault role | Secrets are delivered as environment variables (`CKV_K8S_35` is an accepted weakness in the trust policy) | Secrets mounted as files, and short-lived dynamic database credentials from Vault's database engine instead of static role passwords |
| Bootstrap credentials are generated once and never enter Git | Stored in plain text on the workstation (`.local/secrets/`) | Generated into and read from Vault or a password manager; nothing on disk |

## Identity

| Now | Local simplification | For production |
|---|---|---|
| Keycloak realms for the platform (`platform`) and the workload (`commerce`), with RS256 tokens that every service checks itself | People obtain tokens with the password grant (`sscp-cli`, `commerce-cli`); test personas only | Authorization-code flow with PKCE for people, MFA, accounts from the organisation's identity provider |
| Gitea keeps its own accounts | No single sign-on between Gitea, Keycloak, Harbor and Argo CD | One identity provider for every tool, so a leaver is removed everywhere at once |
| CI zones are Keycloak clients and Vault AppRoles bound to runner addresses | Identity by network address on one Docker host | Per-job workload identity (OIDC tokens or SPIFFE) issued by the runner platform |

## Signing, provenance and the registry

| Now | Local simplification | For production |
|---|---|---|
| Cosign signatures and in-toto attestations (SLSA-style provenance, trust decision, SBOM) as offline Sigstore bundles, verified by Kyverno with the public key | No transparency log (Rekor) or timestamp authority, so a signature does not prove when it was made; the Control Plane audit log and Vault's audit device are the local record | Rekor (public or private) and a timestamp authority, so signing events are publicly or organisation-wide verifiable |
| One Transit key version signs every release | Key rotation is manual, and the public key is distributed to Kyverno by the bootstrap | A rotation procedure with overlapping validity, and the public key distributed through Git and verified at admission |
| Harbor with separate candidate, trusted and tool projects, one robot per role, and immutable release tags in the trusted project | Harbor's internal components talk over plain HTTP inside its Compose network; one Harbor instance | Harbor with TLS between its components and replication to a second site |
| Provenance is generated and signed by the platform, not by the build job | The format follows SLSA provenance v1, but no formal SLSA level is claimed or audited | An assessment against the SLSA build track, with a hardened, isolated builder |

## Kubernetes

| Now | Local simplification | For production |
|---|---|---|
| Kyverno and a native admission policy refuse unsigned images, insecure pod settings, new exposure, secret writers and debug containers; Pod Security `restricted`; default-deny network policies in the application namespaces | The automation's kubeconfig is cluster-admin and can change policies directly (Argo CD restores them from Git) | Cluster-admin only for break-glass use; policy changes only through reviewed Git changes |
| Platform add-ons (Argo CD, Kyverno, External Secrets, Trivy Operator) from pinned chart versions | Their images come from upstream registries, not from Harbor's mirror by digest; the platform namespaces have no default-deny network policies | Add-on images mirrored and pinned by digest; network policies for every namespace |
| Data services (PostgreSQL, Redis, RabbitMQ, MinIO) run in `commerce-data` from mirrored images | Single instances on local-path volumes; connections inside the cluster are not encrypted | Managed or replicated data services with backups; TLS (or a service mesh with mTLS) for every connection |

## Network and TLS

| Now | Local simplification | For production |
|---|---|---|
| Every factory service (Gitea, Harbor, Vault, Keycloak, MinIO, the Control Plane) serves TLS with certificates from a local CA, verified by every client | The CA is created by `sscp up` and trusted by the platform's containers and cluster node; it publishes no revocation information, which is why Windows `curl` needs `--ssl-no-revoke` | Certificates from the organisation's PKI, with revocation and automated renewal |
| Published ports are bound to `127.0.0.1` only | Host access through `localhost` ports | Access through authenticated ingress, never directly to service ports |
| Inside the cluster, the commerce services talk plain HTTP, restricted by network policies | No encryption between pods; the Control Plane's metrics port is plain HTTP on internal networks | mTLS between services (for example a service mesh) |

## Observability

| Now | Local simplification | For production |
|---|---|---|
| Metrics, logs and Kubernetes events in Prometheus and Loki; alert rules and dashboards in Git; release identity on every workload signal ([observability.md](observability.md)) | Single instances with seven days on small local volumes; no Alertmanager, so alerts are only visible, never sent; no trace store | Highly available, long-term storage; Alertmanager routing to on-call; a trace backend |
| Prometheus runs in the cluster | Factory telemetry (Control Plane, Harbor, Vault, Gitea) is collected only while the cluster runs | Monitoring outside the systems it monitors, so a cluster failure stays visible |
| No runtime threat detection | Runtime signals are the workload's own security events, admission metrics and image rescans | A runtime detection tool (for example Falco) feeding the same alerting |

## Audit and evidence

| Now | Local simplification | For production |
|---|---|---|
| The Control Plane's audit log is hash-chained and can be verified end to end; raw evidence reports are in an object-locked bucket | Object lock uses GOVERNANCE mode with 30 days of retention, which an administrator can lift; the chain head lives in the same database, so a database superuser could rewrite the chain consistently | COMPLIANCE-mode retention matching the audit period, and the chain head anchored outside the database (a transparency log or a write-once store) |

## Backup and disaster recovery

| Now | Local simplification | For production |
|---|---|---|
| All state lives in named Docker volumes and the kind node; `sscp up` restores services after a restart, and `sscp verify recovery` proves the Control Plane and Vault recover ([failure-and-recovery.md](failure-and-recovery.md)) | No backups. `sscp reset` and a rebuild recreate the platform, but lose history, evidence and the signing key | Scheduled, tested backups of PostgreSQL, MinIO (evidence), Vault (including the Transit key's encrypted backup), Gitea and Harbor, with regular restore drills |

## Settings for a slow workstation disk

These settings exist only so that the platform runs reliably on a workstation, including
one whose Docker data lives on a hard disk. Each trades durability or strictness for speed
and must be reverted in production.

| Setting | Where | Effect | For production |
|---|---|---|---|
| `synchronous_commit=off` and `recovery_init_sync_method=syncfs` | `platform/compose.yaml` (platform PostgreSQL) | commits return before they are flushed; a crash can lose the last fraction of a second of writes (never corrupt data); start-up after an unclean stop is faster | default `synchronous_commit=on` |
| etcd `--unsafe-no-fsync` | `cluster/kind.yaml` | the cluster's state store does not wait for the disk; a host crash can corrupt the cluster state | never; managed Kubernetes or etcd on fast disks |
| Grafana's database in memory | `cluster/platform/observability/grafana.yaml` | start-up takes seconds instead of minutes; nothing is lost because everything shown is provisioned from Git | a persistent database (for users, annotations) |
| Longer health-check timeouts (Argo CD repo server, Control Plane, Grafana start-up probe) and a 20-second Prometheus scrape timeout | `cluster/helm/argocd.yaml`, `platform/compose.yaml`, `grafana.yaml`, `observability/config/prometheus.yml` | a busy disk does not get healthy processes restarted or scrapes counted as failures | tuned to the real platform's latencies |
| Runners wait up to five minutes for their nested Docker daemon | `platform/runner/docker-run` | runners start after a host restart even when the daemon starts slowly | ephemeral runners on dedicated hosts |
| Vault's health check uses a small HTTP client instead of the `vault` binary, and Vault's memory limit (768 MiB) leaves room to keep its 500 MB binary cached | `platform/compose.yaml` | Vault's code is not repeatedly evicted and re-read from disk, which stalled Vault whenever the disk was busy | platform-level health checks; memory sized from measurements |
| Trivy Operator runs one scan at a time | `cluster/helm/trivy-operator.yaml` | reports for a new release appear over several minutes | parallel scans |
