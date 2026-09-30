# Design decisions

This document records the important architectural decisions of the platform. Each entry
states:

- **Decision**: what was chosen;
- **Why**: the reason;
- **Alternatives**: the main options considered and why they lost;
- **Trade-offs**: what the choice costs.

Decisions about the commerce workload's internal design are in
[workload-architecture.md](workload-architecture.md). What production would change is in
[production-considerations.md](production-considerations.md).

| Area | Decisions |
|---|---|
| CI and trust zones | [DD-01](#dd-01-security-pipelines-are-platform-owned-and-dispatched-by-the-control-plane), [DD-02](#dd-02-one-private-rootless-docker-daemon-per-ci-zone), [DD-03](#dd-03-zone-identities-are-bound-to-runner-network-addresses), [DD-20](#dd-20-ci-tools-come-from-the-platforms-own-registry), [DD-22](#dd-22-scanners-get-a-copy-of-the-source-and-no-network) |
| Evidence and scanning | [DD-06](#dd-06-the-control-plane-reads-reports-itself), [DD-09](#dd-09-trivy-is-the-blocking-scanner-grype-is-secondary-evidence), [DD-10](#dd-10-checkov-owns-iac-hadolint-owns-dockerfile-style-no-kubescape), [DD-11](#dd-11-sonarqube-analyses-main-only), [DD-21](#dd-21-code-authors-cannot-suppress-findings), [DD-23](#dd-23-the-platform-chooses-the-base-images), [DD-24](#dd-24-evidence-describes-the-pushed-digest-not-the-local-build), [DD-25](#dd-25-offline-vulnerability-data-with-an-age-limit), [DD-26](#dd-26-dynamic-tests-run-in-a-throw-away-environment-inside-the-security-zone) |
| Control Plane | [DD-17](#dd-17-the-control-plane-cannot-delete-its-own-history), [DD-18](#dd-18-the-trust-policy-is-built-into-the-control-plane-image), [DD-19](#dd-19-health-and-metrics-on-a-separate-internal-port) |
| Signing and promotion | [DD-04](#dd-04-signing-through-vault-transit-with-per-release-grants), [DD-05](#dd-05-build-once-promote-the-exact-digest), [DD-27](#dd-27-the-control-plane-writes-the-attestations-the-trust-zone-signs-them), [DD-28](#dd-28-offline-sigstore-bundles-as-oci-referrers) |
| Deployment and cluster | [DD-29](#dd-29-kyverno-cel-policies-with-a-native-policy-where-kyverno-cannot-see), [DD-30](#dd-30-external-secrets-authenticates-to-vault-with-offline-checked-cluster-tokens), [DD-31](#dd-31-a-deployment-is-checked-against-git-not-against-argo-cds-summary), [DD-32](#dd-32-gitops-pull-requests-pass-a-deployment-security-gate) |
| Runtime and infrastructure | [DD-07](#dd-07-factory-on-docker-compose-deployment-target-on-kind), [DD-08](#dd-08-local-pki-and-the-sscptest-zone), [DD-12](#dd-12-minio-from-chainguards-source-build), [DD-13](#dd-13-harbor-from-its-official-generator), [DD-14](#dd-14-one-keycloak-with-separate-realms), [DD-15](#dd-15-observability-runs-in-the-cluster), [DD-16](#dd-16-no-public-transparency-log-no-falco), [DD-33](#dd-33-release-identity-travels-with-the-workloads-telemetry) |

## DD-01 Security pipelines are platform-owned and dispatched by the Control Plane

**Decision.** The application repository only defines its validation workflow. Source
scanning, image builds, artifact scanning, dynamic scanning and release are workflows
in the platform repository. The Security Control Plane receives signed Gitea webhooks
(pull request, push to `main`, protected tag) and dispatches the matching platform
workflow with the target commit as input.

**Why.** Gitea runners are chosen by label, and any workflow can ask for any label that
is available to its repository. If evidence-producing jobs were defined in the
application repository, a pull request could add a job that runs on a privileged
runner and forges evidence or reads its credentials. Moving these workflows to a
repository that application developers cannot write to turns "who may change the
security pipeline" into an ordinary repository permission.

**Alternatives.** Label-based zones inside the application repository (simpler, but a
PR can target any zone); per-job approval gates (Gitea only offers approval for fork
PRs); OIDC job tokens bound to refs (Gitea does not issue OIDC tokens for Actions jobs).

**Trade-offs.** The Control Plane becomes the pipeline trigger, so it must be available
for pipelines to start; if it is down, required statuses never appear and merges and
releases are blocked (fail closed). Developers see security results as commit statuses
rather than as jobs in their own repository.

## DD-02 One private rootless Docker daemon per CI zone

**Decision.** Each runner uses the `gitea/runner` rootless Docker-in-Docker image. Jobs
run in that runner's own daemon. Validation jobs get no Docker socket; security and
build jobs get the socket of their zone's daemon only.

**Why.** Exposing the host Docker socket to a job is equivalent to giving it root on the
host. A private daemon per zone also isolates image caches, build caches and volumes
between untrusted and trusted contexts, and gives each zone a stable network identity.

**Alternatives.** Host-socket runners (unsafe), one shared DinD daemon (shared cache,
shared blast radius), Kubernetes-based runners (would make CI depend on the cluster CI
is supposed to deploy to), VM-per-job (not available on Docker Desktop).

**Trade-offs.** Runner containers run privileged so that they can host a nested daemon;
the nested daemon itself runs rootless, and jobs are never privileged. Images are pulled
once per zone, costing disk.

## DD-03 Zone identities are bound to runner network addresses

**Decision.** Runners have fixed IP addresses on `sscp-edge`. Each zone's Vault AppRole
has `token_bound_cidrs` and `secret_id_bound_cidrs` set to its runner's address. Jobs
exchange the AppRole for a short-lived token and read only their zone's secrets.

**Why.** Rootless Docker translates every job container's traffic to its runner's
address, so Vault can check where a request came from. A stolen zone credential is
useless from any other runner or container.

**Trade-offs.** Network identity is weaker than workload attestation (for example
SPIFFE); it relies on the Docker network not being spoofable, which holds on a single
Docker host. Production would use per-job OIDC or SPIFFE identities.

## DD-04 Signing through Vault Transit with per-release grants

**Decision.** Cosign signs with `hashivault://cosign-commerce`, a non-exportable ECDSA
P-256 Transit key. The trust zone receives a single-use, response-wrapped AppRole
`secret_id` for the `trust-signer` role from the Control Plane for each approved
release.

**Why.** The private key never exists outside Vault. Signing ability is tied to a
positive trust decision, not to a runner being online, and every signature appears in
Vault's audit log next to the release identifier stored in the `secret_id` metadata.

**Alternatives.** Cosign key file in a CI secret (key can be copied), keyless signing
with Fulcio and Rekor (needs public internet services), a self-hosted Sigstore stack
(large footprint for a workstation).

**Trade-offs.** No transparency log. Revocation is key rotation plus Kyverno policy
update, not a certificate revocation list.

## DD-05 Build once, promote the exact digest

**Decision.** Images are built once in the build zone and pushed to
`commerce-candidates`. At release, the trust zone copies the same manifest by digest to
`commerce-trusted` with `crane copy`, then signs and attests it there. No release rebuild
ever happens.

**Why.** Rebuilding at release time would produce an artifact whose evidence was never
collected. Copying by digest guarantees that what was scanned is what runs.

## DD-06 The Control Plane reads reports itself

**Decision.** CI uploads raw scanner reports; the Control Plane stores them (MinIO),
records their SHA-256, parses them with scanner-specific readers, checks that the
report refers to the claimed commit or digest, and computes finding summaries.

**Why.** If CI supplied the counts, a compromised job could claim "zero findings".
Parsing server-side also makes evidence consistency checks (right digest, right
scanner, complete set) enforceable in one place.

**Trade-offs.** The Control Plane has to understand each report format. It still does
not execute scanners.

## DD-07 Factory on Docker Compose, deployment target on kind

**Decision.** Gitea, runners, Harbor, Vault, Keycloak, the Control Plane, SonarQube,
PostgreSQL and MinIO run on Docker Compose. Argo CD, Kyverno, ESO, the workload and
observability run in kind. kind nodes join the `sscp-edge` Docker network.

**Why.** The factory must exist before the cluster and must survive a cluster reset.
Putting both on one Docker network lets the cluster use the same host names and
certificates as CI.

**Alternatives.** Everything in Kubernetes (cluster reset would wipe the registry and
CI; a chicken-and-egg bootstrap), a second cluster for the factory (memory cost).

## DD-08 Local PKI and the `.sscp.test` zone

**Decision.** The bootstrap creates a local root CA and issues TLS certificates for
Gitea, Harbor, Vault, Keycloak, MinIO and the Control Plane. Docker network aliases
resolve `*.sscp.test` for containers and cluster nodes; the host uses `localhost` ports.

**Why.** Registry, signing and identity traffic carry credentials and must be
authenticated. `.test` is reserved and can never collide with a real domain.

**Trade-offs.** Browsers on the host need either the local CA imported or a certificate
warning accepted; host access goes through `https://localhost:<port>`.

## DD-09 Trivy is the blocking scanner, Grype is secondary evidence

**Decision.** Trivy and Grype both scan each pushed image, reading it from the registry by
digest. Both reports are mandatory evidence, but only Trivy's findings drive the
vulnerability gate and the remediation SLA. Grype's findings are stored as supporting
evidence and shown in the decision; they do not block by themselves.

**Why.** Two blocking scanners with different databases produce contradictory gates and
twice the exception work, for little extra assurance. A second scanner with an independent
database still gives a second opinion that reviewers can compare. Making its evidence
mandatory means a broken second scanner is noticed rather than silently missing.

**Alternatives.** Trivy alone (no second opinion); both blocking (contradictory gates);
Grype scanning the SBOM instead of the image (would miss anything the SBOM generator did
not catalogue).

**Trade-offs.** Two vulnerability databases must be mirrored and kept fresh. A finding
only Grype reports does not block, so reviewers must read Grype's results to benefit from
them.

## DD-10 Checkov owns IaC, Hadolint owns Dockerfile style, no Kubescape

**Decision.** Checkov scans the application repository with its `dockerfile`,
`kubernetes` and `kustomize` frameworks, and the rendered overlays of the GitOps
repository with `kustomize`. Hadolint lints the Dockerfile. After deployment, the Trivy
Operator rescans the running images and workload configuration. Kubescape is not used.

**Why.** Each concern has exactly one owner. Kubescape would overlap with Checkov on
manifests and with the Trivy Operator on the cluster, without closing a gap that either
leaves open.

**Trade-offs.** Checkov's free edition does not grade its checks, so every failure counts
as High, and a few checks are downgraded in the trust policy with written reasons
([trust-policy.md](trust-policy.md#checkov-downgrades)).

## DD-11 SonarQube analyses `main` only

**Decision.** SonarQube Community Build analyses `main` builds and its quality gate is
recorded as evidence. Pull requests rely on Semgrep for fast SAST feedback.

**Why.** The Community Build does not support pull request or branch analysis.

## DD-12 MinIO from Chainguard's source build

**Decision.** MinIO runs from `cgr.dev/chainguard/minio`, pinned by digest.

**Why.** MinIO stopped publishing community container images. A digest-pinned build
from a supplier that builds from source keeps MinIO without depending on unmaintained
images.

## DD-13 Harbor from its official generator

**Decision.** The bootstrap runs Harbor's own `prepare` image to generate configuration
and a Compose file, then adjusts the generated Compose file (named volumes instead of
host paths, standard logging, joining `sscp-edge`).

**Why.** Harbor's configuration is large and version-specific. Generating it with the
official tool keeps it correct across upgrades; the post-processing is small and
confined to the `generate()` step of `automation/src/sscp/services/harbor.py`.

## DD-14 One Keycloak with separate realms

**Decision.** One Keycloak instance hosts the `commerce` realm (workload users and the
gateway/API audience) and the `platform` realm (Control Plane users and CI zone
clients). Client secrets and user passwords are generated at bootstrap and stored in
Vault or `.local/secrets`, never in realm files.

**Why.** Realms are fully isolated issuers; a second Keycloak would cost memory
without adding isolation.

## DD-15 Observability runs in the cluster

**Decision.** Prometheus, Grafana, Loki and the OpenTelemetry Collector run in the
`observability` namespace, managed by Argo CD like the rest of the cluster's security
configuration. Prometheus also scrapes the Control Plane, Harbor, Vault and Gitea over
`sscp-edge`. All scrape targets are fixed names (Kubernetes service names in the cluster,
platform host names outside it).

**Why.** The deployment-side signals (admission, GitOps, secret delivery, runtime
vulnerabilities) come from components inside the cluster, and the workload can reach a
collector in the cluster without leaving it. The factory services are few and have fixed
addresses. Fixed targets instead of Kubernetes service discovery mean Prometheus needs no
Kubernetes API access at all.

**Alternatives.** A separate observability stack in Docker Compose next to the factory:
it would keep factory telemetry when the cluster is down, but the workload's telemetry
would have to leave the cluster and cross into the factory network.

**Trade-offs.** Factory telemetry is only collected while the cluster is running. A new
scrape target needs a configuration change instead of appearing automatically.
Details: [observability.md](observability.md).

## DD-16 No public transparency log, no Falco

Signatures stay local (see DD-04). Runtime threat detection (Falco) is outside the
scope; structured security events from the workload and admission metrics cover the
runtime signals the platform demonstrates. Both are listed in
[production-considerations.md](production-considerations.md).

## DD-17 The Control Plane cannot delete its own history

**Decision.** The Control Plane connects to PostgreSQL as `controlplane_app`, which may
read, insert and update lifecycle records but only read and insert evidence, findings,
decisions, signatures, promotions, deployments and audit entries, and may delete
nothing. Migrations run with the owner role in a one-off container. The audit log is a
SHA-256 hash chain written under an advisory lock, and raw reports are re-hashed at every
evaluation.

**Why.** The Control Plane is the component an attacker would most like to lie through.
Removing its ability to delete or rewrite history means a compromise can add false
records at worst, and those are visible in the audit chain; changes made with higher
database privileges break the chain and are detected by `GET /api/audit/verification`.

**Alternatives.** An external append-only ledger or transparency log (stronger, but a
second system to operate), database triggers that reject updates (equivalent protection,
but hidden logic in SQL instead of privileges that can be inspected).

**Trade-offs.** The chain detects tampering but cannot prevent a database superuser from
rewriting the whole chain consistently; production would anchor the chain head outside
the database (see [production-considerations.md](production-considerations.md)).

## DD-18 The trust policy is built into the Control Plane image

**Decision.** `policy/trust-policy.yaml` and `policy/applications.yaml` are copied into
the Control Plane image. Every decision records the policy version, derived from the
file hash.

**Why.** Policy is reviewed like code in the platform repository, and the running
service can only apply a policy that went through that path. Recording the version with
each decision makes past decisions explainable after the policy changes.

**Alternatives.** Loading policy from a mounted directory (editable without review),
storing policy in the database behind an admin API (needs its own approval workflow),
OPA/Rego (a second policy language and runtime for rules that are simple data).

**Trade-offs.** A policy change needs an image rebuild and restart, which `sscp up` does.

## DD-19 Health and metrics on a separate internal port

**Decision.** The Control Plane serves its API only on the HTTPS port and serves health
and Prometheus metrics only on a plain-HTTP management port that is not published on the
host.

**Why.** Unauthenticated endpoints never share a listener with the API, and bearer
tokens are never accepted over plain HTTP. Prometheus and container health checks do not
need TLS client configuration.

**Trade-offs.** Anyone on the internal Docker networks can read the metrics; they
contain counts, not evidence.

## DD-20 CI tools come from the platform's own registry

**Decision.** `sscp up --with ci` copies the `linux/amd64` manifest of every pinned CI
image into Harbor's `platform-tools` project and verifies the digest. Runners, scanners
and builds pull only from there. The CI helper, rules and tool list are packaged into a
`ci-tools` image that runners reference by digest.

**Why.** Pipelines keep working offline, a registry outage or a re-pushed upstream tag
cannot change what runs, and every tool is traceable to the pin in `versions.yaml`.
Copying only the platform the workstation needs saves most of the space multi-platform
indexes would take.

**Alternatives.** Pulling from upstream registries during each job (internet dependency,
rate limits), a Harbor proxy-cache project (caches whatever is requested rather than the
pinned set), pre-loading images into each runner daemon with `docker load` (no single
place to inspect what runs).

**Trade-offs.** The mirrored manifest digest is the per-platform digest, not the index
digest written in `versions.yaml`; the mirror records both and checks their relation. The
first mirror downloads about 4 GB.

## DD-21 Code authors cannot suppress findings

**Decision.** Every scanner ignores suppressions written by the code's authors: Semgrep
runs with `--disable-nosem`, Gitleaks with the platform's own configuration and
`--ignore-gitleaks-allow`, Hadolint with `--disable-ignore-pragma`; the CI helper deletes
repository-level scanner configuration and ignore files from the job's copy of the source;
and the Control Plane counts checks skipped by `checkov:skip` comments as failed. It reads
every finding from the raw report. The only ways to let a finding through are a
time-limited, separately approved risk exception, or a change to the platform-owned
rules or policy.

**Why.** An inline comment or a `.gitleaks.toml` in the repository is written by the same
person whose code is being checked, so it would let an application change weaken its own
security gate. Each tool honours such files by default.

**Trade-offs.** Genuine false positives need a platform change (or an exception) instead
of a one-line comment; the platform rules therefore recognise safe-by-construction
patterns explicitly (for example `PrivilegeStatements` for SQL identifiers), each backed
by a rule test.

## DD-22 Scanners get a copy of the source and no network

**Decision.** The CI helper copies the fetched commit into a job-private Docker volume
and runs each source scanner as a sibling container with `--network none`, all
capabilities dropped and `no-new-privileges`. Reports are copied back out and the volume
is deleted.

**Why.** A scanner parses untrusted code. Without network access it cannot download
rules at run time or leak the code, and it sees nothing of the job beyond its inputs. The
approach also works whatever workspace layout the runner uses.

**Trade-offs.** Each scan copies the source tree once more; for this repository that is
a fraction of a second.


## DD-23 The platform chooses the base images

**Decision.** Application Dockerfiles take their base images only from the build
arguments `SDK_IMAGE` and `RUNTIME_IMAGE`. The build zone sets them to the digest-pinned
copies in `platform-tools` and refuses a Dockerfile whose stages start from anything
else, that copies or mounts files from an external image, or that adds remote content.

**Why.** The base image is most of what ships. Keeping its choice in `versions.yaml`
(reviewed by the platform team) and enforcing it before the build means an application
change cannot swap in an unreviewed base image, and builds run without pulling from
public registries.

**Alternatives.** Literal digests in each Dockerfile (the application team controls
them), a Checkov rule alone (Checkov cannot resolve build arguments; see the
`CKV_DOCKER_7` downgrade in the trust policy, which relies on this check).

**Trade-offs.** Dockerfiles must follow the convention; stages that need other tools have
to install them from the platform base images.

## DD-24 Evidence describes the pushed digest, not the local build

**Decision.** The build zone pushes each image first and registers the digest the
registry returned. Syft, Trivy and Grype then read the image from the registry by that
digest, and the Control Plane refuses any report that does not name it.

**Why.** A local image and the pushed manifest can differ (for example in compression or
attestations). Scanning what the registry serves, by digest, means the evidence is about
exactly the artifact that can later be signed and deployed.

**Trade-offs.** Each scanner downloads the image layers again; the security zone keeps
its scanner databases in cache volumes so only the image itself is fetched.

## DD-25 Offline vulnerability data with an age limit

**Decision.** The Trivy and Grype databases are copied into Harbor by the bootstrap and
refreshed with `sscp tools refresh-db`. Scanners in the security zone use only those
copies. The trust policy refuses vulnerability evidence from a database older than seven
days.

**Why.** Scans stay reproducible and work offline, and the security zone needs no
internet access. The age limit turns a missed refresh into a visible, blocking failure
rather than quietly outdated results.

**Alternatives.** Letting scanners download their databases in each job (internet
dependency and rate limits), a long-lived database volume without an age check (silently
stale results).

**Trade-offs.** Someone has to refresh at least weekly; vulnerability data is only as
current as the last refresh.

## DD-26 Dynamic tests run in a throw-away environment inside the security zone

**Decision.** For every main build, the security zone starts the candidate images (by
digest) with their data services on a private network in its own Docker daemon, runs the
platform's authorization suite and an authenticated ZAP API scan through the gateway,
and removes everything afterwards.

**Why.** The tests exercise exactly the artifacts that would be signed, with the
workload's real authorization (tokens from the platform Keycloak), without depending on
the Kubernetes cluster that only receives signed images later. Keeping the environment in
the security zone means no other zone, and no long-lived environment, ever runs
unverified images.

**Alternatives.** A standing test namespace in the cluster (the cluster would have to
admit unsigned candidates, weakening admission control), testing only in the developer's
workload profile (not the built images), DAST against production-like staging after
deployment (too late to block trust).

**Trade-offs.** The security runner needs more memory while the environment runs; its
container is allowed 7 GiB for that reason. The dynamic job is the longest part of the
main pipeline: active ZAP scanning alone may take up to 10 minutes.

## DD-27 The Control Plane writes the attestations; the trust zone signs them

**Decision.** The provenance, trust-decision and SBOM predicates attached to each image
are assembled by the Control Plane from its own records and handed to the release run;
the trust zone only signs and attaches them.

**Why.** The trust zone did not observe the build and must not be trusted to describe it.
The Control Plane holds the authoritative build, evidence and decision records (with
report hashes it re-checks), so the attestations cannot drift from what was actually
decided.

**Alternatives.** Provenance generated inside the build job (the build zone would sign
claims about itself and would need signing rights), or re-deriving the facts in the trust
zone from registry and Gitea metadata (a second, weaker source of truth).

**Trade-offs.** The provenance is signed after the build rather than by the build
platform during the build, so the platform does not claim a SLSA Build level (see
[release-signing.md](release-signing.md#why-slsa-style)).

## DD-28 Offline Sigstore bundles as OCI referrers

**Decision.** Cosign runs with `--tlog-upload=false --use-signing-config=false
--new-bundle-format=true`: no Rekor or Fulcio; the signature and each attestation are
Sigstore bundles stored as OCI 1.1 referrers of the image in the trusted project.
Verifiers use the public key file with `--insecure-ignore-tlog=true`.

**Why.** The platform must work without internet services (DD-16). Kyverno's image
verification accepts an attestation only when Cosign reports its bundle as verified;
attestations in Cosign's older tag-based layout, signed without a transparency log, can
never meet that. Bundles verify offline against the key alone.

**Alternatives.** Cosign's tag-based layout (`.sig` / `.att` tags; its attestations fail
admission offline, and Cosign marks the format as deprecated), or a self-hosted Rekor
(large footprint for a workstation).

**Trade-offs.** No independent, append-only record of signing times; the Control Plane
audit log and Vault's audit device are the local substitute. Referrers are not copied by
a plain `crane copy`, so the trust zone signs the image after promoting it (DD-05).

## DD-29 Kyverno CEL policies, with a native policy where Kyverno cannot see

**Decision.** Admission rules are Kyverno `ImageValidatingPolicy` and `ValidatingPolicy`
resources (CEL expressions, `failurePolicy: Fail`), plus one native Kubernetes
`ValidatingAdmissionPolicy` that forbids ephemeral containers. They are platform-owned
and synced by Argo CD's `platform` project.

**Why.** Kyverno verifies Cosign signatures and attestations at admission, which the API
server cannot do alone. Its classic `ClusterPolicy` API is deprecated in the pinned
version; the CEL types are its current API. Ephemeral containers arrive through a
subresource those CEL types are not called for, so that rule is enforced by the API
server itself.

**Alternatives.** Kyverno `ClusterPolicy` (deprecated), OPA Gatekeeper with a separate
image-verification provider, Sigstore's policy-controller (signatures only, no general
workload rules).

**Trade-offs.** Two admission mechanisms to understand. The Pod Security Standard
`restricted` overlaps with parts of `workload-security`; both are kept, the policy with
clearer messages and stricter rules (read-only root file system, no token).

## DD-30 External Secrets authenticates to Vault with offline-checked cluster tokens

**Decision.** External Secrets logs in to Vault's `jwt-kind` auth method with short-lived
tokens of each namespace's `vault-secrets` service account. Vault verifies them with the
cluster's service-account public key; each namespace maps to its own read-only policy.

**Why.** Vault needs no credentials for the cluster and no network path into it, and the
identity is the namespace, which is also where Secrets can be mounted.

**Alternatives.** Vault's Kubernetes auth (Vault must call the cluster's TokenReview API
with a long-lived reviewer token), a static Vault token in the cluster, one store per
workload (no stronger isolation, since any pod in the namespace can mount any Secret).

**Trade-offs.** Offline checks cannot notice a deleted service account until its token
expires (minutes). Recreating the cluster changes its key; `sscp up --with cluster`
writes the new one to Vault.

## DD-31 A deployment is checked against Git, not against Argo CD's summary

**Decision.** Argo CD reports each healthy sync (revision, sync and health status) to the
Control Plane with a shared token. The Control Plane reads that revision's overlay from
the GitOps repository and marks the release deployed only if the pinned images are
exactly the digests in its own records.

**Why.** The Git revision is content-addressed and is what Argo CD applied; Argo CD's
image summary can lag behind or include terminating pods. Reading Git also catches a
GitOps commit that points at other signed images than the release approved.

**Alternatives.** Trusting the images Argo CD lists, having the Control Plane poll the
cluster (it would need cluster credentials), or Argo CD pushing with a Keycloak token
(its notification controller cannot obtain one).

**Trade-offs.** The shared token is a second credential type besides Keycloak tokens. A
report Argo CD failed to deliver is not retried for the same revision; re-sending it is
a manual step (see [troubleshooting](gitops-and-admission.md#operating)).

## DD-32 GitOps pull requests pass a deployment-security gate

**Decision.** Pull requests on the GitOps repository are checked like application code:
the Control Plane dispatches `deployment-pipeline` in the security zone, which runs
Gitleaks and Checkov on the rendered Kustomize overlays, and sets the required
`sscp/deployment-security` status from its decision. The release bot's own commits only
change digests and go straight to `main`.

**Why.** Desired state is code too; an insecure manifest should be refused before it is
merged, with a reason a reviewer can read, not only when Argo CD applies it and Kyverno
refuses it.

**Alternatives.** Relying on admission control alone (feedback only after merge), or a
workflow inside the GitOps repository (its authors could change the checks, see DD-01).

**Trade-offs.** Checkov and Kyverno partly overlap. Checkov only sees the GitOps
repository, so checks answered by platform-owned configuration (network policies) are
downgraded in the trust policy with the reason stated.

## DD-33 Release identity travels with the workload's telemetry

**Decision.** The release pipeline commits a `commerce-release` ConfigMap (tag, commit,
release id) with every release's digests. The Deployments pass tag and commit to the
OpenTelemetry SDK as resource attributes, and the collector adds the pod's namespace,
Deployment and running image from the Kubernetes API. These become Prometheus labels and
Loki index labels.

**Why.** A runtime signal (an error spike, tampered messages, a new vulnerability) is only
actionable in a supply-chain context if it names the release and digest behind it; from
the digest, the Control Plane's trace endpoint leads to the evidence and decisions.

**Alternatives.** Baking the tag into the image at build time: the same digest can be
released under more than one tag (a re-release), so the tag is a property of the
deployment, not of the image. Deriving it from the image digest alone in dashboards would
need a lookup against the Control Plane for every query.

**Trade-offs.** The tag and commit are reported by the pods themselves; the image and pod
metadata come from the collector, which a pod cannot influence. Only an allow-list of
resource attributes becomes labels, to keep the number of series small.
