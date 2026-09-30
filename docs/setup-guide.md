# Setup guide

This guide takes a workstation from nothing to a running platform with a first release
deployed to the cluster. It then explains how to verify the platform, how to stop and
restart it, and how to remove it completely. Every command is meant to be copied and run
as written.

**Time needed:** about 45–90 minutes for the first start (mostly downloading pinned images
once), plus about 30 minutes for the first main build on a typical workstation.

## 1. Prerequisites

**Operating system.** The platform is built and verified on **Windows 11 with Docker
Desktop (WSL 2 backend)**, using Git Bash for the shell examples. The automation is Python
and mostly portable. However, the pinned kind download in `versions.yaml` is the Windows
amd64 binary, so running on Linux or macOS would first need a pinned kind entry (URL and
SHA-256) for that system.

| Requirement | Version | Why it is needed |
|---|---|---|
| Docker Desktop (WSL 2 backend on Windows) | recent | Every platform service, the four CI zones and the kind cluster run as containers |
| Memory for Docker | **12 GiB minimum, 16 GiB recommended** | About 10 GiB is in use with everything started; a main pipeline adds several GiB while it builds and tests. Below 16 GiB the Docker VM swaps during pipelines: it works, but slowly. |
| CPUs for Docker | 4 or more | Builds, scanners and the cluster run in parallel |
| Free disk space | **50 GiB recommended** (30 GiB minimum) | Images, volumes and build cache take about 45 GB with everything started |
| Python | 3.12 or newer | The `sscp` automation |
| [uv](https://docs.astral.sh/uv/) | recent | Runs the automation from its lock file (`uv.lock`) in an isolated environment |
| git | 2.40 or newer | Publishing the repositories to Gitea |
| kubectl | 1.36 | Operating the kind cluster |
| Helm | 4.x | Installing Argo CD, Kyverno, External Secrets Operator and the Trivy Operator |
| .NET SDK | 10.0.401 (optional) | Only for building and testing the .NET code on the host; CI builds in containers |

kind itself does not need to be installed: `sscp` downloads the pinned version into
`.tools/bin/` and accepts it only if its SHA-256 matches `versions.yaml`.

**Windows: give Docker enough memory.** The memory Docker can use is set in
`%USERPROFILE%\.wslconfig`:

```ini
[wsl2]
memory=16GB
```

Then restart WSL so the setting takes effect:

```bash
wsl --shutdown
```

**Internet access** is needed to download pinned images, charts and NuGet packages. The
pipelines themselves pull every tool from the platform's own registry, not from the
internet.

## 2. Check the workstation

All `sscp` commands run from the `supply-chain-platform` directory of the workspace:

```bash
cd supply-chain-platform
uv run sscp doctor
```

`doctor` checks Docker (reachable, memory, CPUs), free disk space, the required tools and
the **pinning policy**: every image in
[`versions.yaml`](../supply-chain-platform/versions.yaml) must be pinned by digest, and
every downloaded tool must have a SHA-256. It stops with a clear message if something
blocking is missing.

## 3. Start the platform

Start everything — the software factory, SonarQube, the CI zones and the cluster:

```bash
uv run sscp up --with quality,ci,cluster
```

`sscp up` is **idempotent**: it converges the platform to the configured state, so it is
safe to run again at any time — after a restart, after a change, or after a step failed.
Each step prints what it does and how long it took.

### What `sscp up` does

| Stage | Steps |
|---|---|
| Workstation | issue a local certificate authority and TLS certificates (`.local/pki/`); create the Docker networks `sscp-edge`, `sscp-data` (internal) and `sscp-workload-dev`; generate credentials (`.local/secrets/bootstrap.json`) and the Compose environment file |
| Core services | start PostgreSQL, Vault, Keycloak, MinIO and Gitea; initialise and unseal Vault, create a scoped bootstrap token and revoke the root token, enable the KV, Transit and AppRole engines and the audit device; create Gitea's people, organisations, repositories, branch and tag protections and the webhook; create the object-locked `evidence` bucket; apply the Keycloak realms |
| Registry (always) | generate Harbor's deployment with Harbor's own generator and start it |
| Control Plane (always) | build its image; prepare release signing in Vault (Transit key, `trust-signer` and grant-issuing identities); create its restricted database role and apply migrations; start it |
| `--with quality` | start SonarQube, replace its default password, configure the platform quality gate and the security zone's analysis token |
| `--with ci` | mirror the pinned tool images into Harbor (`platform-tools`); copy the Trivy and Grype vulnerability databases into Harbor; create the candidate and trusted projects and the robot accounts; build the `sonar-dotnet` and `ci-tools` images; create the CI zone identities in Vault; configure and start the four runners |
| `--with cluster` | create the kind cluster on `sscp-edge` and make its container runtime trust Harbor's certificate; install External Secrets, Kyverno, Argo CD and the Trivy Operator from pinned charts; create the cluster's pull-only registry robot; let External Secrets read Vault and store the workload and platform secrets; apply the platform's cluster state and hand it to Argo CD |

### Capabilities

Optional capabilities start only when asked for, to keep memory use down:

| Option | Adds | Approximate memory |
|---|---|---|
| (always) | PostgreSQL, Vault, Keycloak, MinIO, Gitea, Harbor, Security Control Plane | 1.7 GiB |
| `quality` | SonarQube Community Build (needed by main pipelines) | +1–2 GiB |
| `ci` | the tool mirror, vulnerability databases, robot accounts, `ci-tools`, the four zone runners | +0.5 GiB idle; a main pipeline uses several GiB more while it runs |
| `cluster` | kind with Argo CD, Kyverno, External Secrets, the Trivy Operator, observability and (after a release) the workload | about 5 GiB with the workload running |
| `workload` | PostgreSQL, Redis, RabbitMQ and MinIO for running the workload as local processes (development only) | +0.3 GiB |

Main pipelines need `quality` and `ci`. `workload` is only for developing on the host
(see [section 9](#9-developing-the-workload-on-the-host)).

## 4. Endpoints

Every port is bound to `127.0.0.1`, so nothing is reachable from other machines.
Certificates are issued by the platform's own certificate authority for
`<name>.sscp.test` and `localhost`; import `.local/pki/ca.crt` into your browser's trust
store to avoid certificate warnings (Argo CD uses its own self-signed certificate).

| Service | URL | Sign in as |
|---|---|---|
| Gitea | https://localhost:3000 | `sscp-admin` (credential `gitea.admin`) or a persona below |
| Harbor | https://localhost:8443 | `admin` (credential `harbor.admin`) |
| Vault | https://localhost:8200 | bootstrap token (credential `vault.bootstrap-token`) |
| Keycloak | https://localhost:9443 | `bootstrap-admin` (credential `keycloak.bootstrap-admin`) |
| SonarQube | http://localhost:9100 | `admin` (credential `sonarqube.admin`) |
| Security Control Plane API | https://localhost:7443 | a `platform` realm persona (token below) |
| Argo CD | https://127.0.0.1:8444 | `admin`, password in the cluster Secret `argocd-initial-admin-secret` ([how to read it](gitops-and-admission.md#operating)) |
| Commerce gateway | http://127.0.0.1:8088 | a `commerce` realm persona |
| Grafana | http://127.0.0.1:3300 | `admin` (credential `grafana.admin`) |
| Prometheus | http://127.0.0.1:9990 | none (read-only UI and API) |

### Reading a generated credential

Credentials are generated at the first start and kept in the git-ignored file
`.local/secrets/bootstrap.json`. Print one value at a time rather than opening the file:

```bash
uv run python -c "from sscp import credentials; print(credentials.find('gitea.admin'))"
```

Persona passwords use the keys `gitea.user.<name>`, `keycloak.platform.user.<name>` and
`keycloak.commerce.user.<name>`.

### People (personas)

**Gitea accounts** (source control):

| Account | Role |
|---|---|
| `alice` | commerce developer: branches and pull requests |
| `max` | commerce maintainer: the only approver of commerce pull requests |
| `rhea` | release manager: the only account allowed to push `v*` tags |
| `pat` | platform engineer: pushes to the platform repository, opens GitOps pull requests |
| `omar` | platform reviewer: approves GitOps pull requests |

**Keycloak `platform` realm** (Security Control Plane):

| Persona | Roles | Can |
|---|---|---|
| `victor` | platform-viewer | read builds, evidence, decisions, releases and the audit log |
| `rita` | risk-owner, platform-viewer | request risk exceptions |
| `sean` | security-approver, risk-owner, platform-viewer | approve, reject and revoke exceptions requested by others |
| `paula` | platform-admin, platform-viewer | everything a viewer can, plus re-running builds that failed for infrastructure reasons |

**Keycloak `commerce` realm** (the workload): `carol` and `dave` (customers), `sam`
(support agent), `cathy` (catalogue manager), `ivan` (inventory clerk), `olga` (order
manager), `fiona` (finance), `aldo` (auditor) and `ada` (administrator). Their permissions
are listed in [identity-and-authorization.md](identity-and-authorization.md).

A Control Plane token for a persona (password grant through the public `sscp-cli` client):

```bash
uv run python -c "from sscp.services import controlplane; print(controlplane.user_token('victor'))"
```

## 5. Verify the platform

```bash
uv run sscp status
uv run sscp verify foundation controlplane registry ci-isolation
```

The suites send real requests to the running platform and assert what it does and what it
refuses. `signing`, `cluster` and `observability` need a deployed release, so run them after
[section 6](#6-build-and-release-for-the-first-time).

| Suite | Needs | Checks |
|---|---|---|
| `foundation` | default | TLS on every endpoint, name resolution inside the edge network, databases and evidence unreachable from it, per-service database isolation, write-once evidence storage, Harbor health, Vault recovering from a restart |
| `controlplane` | default | real Keycloak tokens with the right meaning (people vs. CI zones), health and metrics only on the internal port, the restricted database role, a read-only, capability-free container, restart with the audit chain intact |
| `ci-isolation` | `ci` | application workflows cannot reach platform runners, validation jobs get no Docker and no credentials, runner scopes, zone identities fail from other addresses and cannot read other zones' secrets, private Docker daemons, unsigned webhooks refused |
| `registry` | `ci` | robot permissions, private candidates, read-only tools, vulnerability databases younger than seven days |
| `signing` | `ci` + a release | non-exportable key, no signing without a grant, grants single-use and runner-bound, promoted images verify, release tags immutable |
| `cluster` | `cluster` + a release | admission refusals, network paths, workload identities, secret scoping, no cluster access from CI, Argo CD project limits, traceability of the running release |
| `observability` | `cluster` + a release | every metrics source scraped, alert rules loaded, security signals arriving with the release identity, vulnerability reports for running images, Grafana dashboards |
| `recovery` | `quality,ci,cluster` | the Control Plane and Vault fail closed while stopped and recover afterwards |

`uv run sscp verify` with no suite name runs all eight (about 15 minutes). Do not run the
suites while a pipeline is building: on a slow disk a build can stall other services long
enough for checks to time out. The full testing picture is in
[testing-strategy.md](testing-strategy.md).

## 6. Build and release for the first time

On a freshly created platform the repositories' first commits reach Gitea before the
Control Plane is running, so nothing has been built yet. Ask for the first main build:

```bash
uv run sscp repo build
```

Gitea delivers the push event for the tip of `main` again, and the Control Plane starts
the main pipeline unless that commit was already built. Follow it in Gitea under
`platform/supply-chain-platform` → **Actions**. When the commit's `sscp/trust-decision`
status is green (`PASS for 6 images`), release it as the release manager:

```bash
uv run sscp repo tag -t v1.0.0
```

The release pipeline promotes, signs and attests the images and commits their digests to
the GitOps repository; Argo CD deploys them within a few minutes. When the tagged commit's
`sscp/release` status reads **`release v1.0.0 deployed to local`**, the release is running:

```bash
kubectl --kubeconfig ../.local/generated/kubeconfig -n commerce get pods
curl http://127.0.0.1:8088/api/catalog/products
```

Then run the remaining suites:

```bash
uv run sscp verify signing cluster observability
```

Release tags are immutable, so each release needs a new tag (`v1.0.1`, `v1.0.2`, …). The
whole flow is explained step by step in [end-to-end-flow.md](end-to-end-flow.md).

## 7. Stop, restart and clean up

| Goal | Command | Effect |
|---|---|---|
| Stop everything, keep all data | `uv run sscp down` | stops the kind node, Harbor and every platform service |
| Start again | `uv run sscp up --with quality,ci,cluster` | restarts everything and **unseals Vault** (Vault always comes back sealed) |
| Remove everything | `uv run sscp reset --yes` | deletes the cluster, every platform container, volume and network, and the whole `.local/` directory |

`reset` deletes the Vault unseal keys and Vault's storage, including the signing key: the
next `sscp up` creates a new installation, and nothing signed by the old key verifies any
more. It refuses to run without `--yes`.

### After a Docker or host restart

Containers restart automatically, but **Vault comes back sealed**, which stops signing,
secret delivery and CI logins. Run `uv run sscp up --with quality,ci,cluster` once; it
unseals Vault with the stored keys and reapplies configuration. If a step times out because
services are still starting, run it again. See [troubleshooting.md](troubleshooting.md) for
other symptoms.

## 8. Everyday tasks

| Task | Command |
|---|---|
| Propose an application change as alice | `uv run sscp repo propose -b feature/x -m "Describe the change"` |
| Approve it as max, merge it as alice | `uv run sscp repo approve -n <n>`, then `uv run sscp repo merge -n <n>` |
| Propose a GitOps change as pat; approve as omar; merge as pat | `uv run sscp repo propose --repo commerce-gitops -b change/x -m "…"`, then `approve` / `merge` with `--repo commerce-gitops` |
| Publish platform repository changes (as pat) | `uv run sscp repo sync -m "Describe the change"` |
| Release | `uv run sscp repo tag -t v1.2.3` |
| Refresh the vulnerability databases (at least weekly) | `uv run sscp tools refresh-db` |

The complete command reference is in [cli-reference.md](cli-reference.md).

## 9. Developing the workload on the host

For a fast inner loop the six services can run as local processes against the `workload`
data services and the platform's Keycloak:

```bash
uv run sscp up --with workload
uv run sscp workload setup     # realm, database roles, signing keys, build, migrations
uv run sscp workload start     # the six services; the gateway listens on http://localhost:5080
uv run sscp workload stop
```

Logs are written to `.runs/workload-dev/<service>.log`. `uv run sscp workload reset`
deletes the development data and prepares it again. This mode is for development only:
anything that reaches the cluster goes through the pipelines. See
[workload-architecture.md](workload-architecture.md).

## Next steps

- [architecture-overview.md](architecture-overview.md) — how the pieces fit together
- [end-to-end-flow.md](end-to-end-flow.md) — what happens to a change
- [cli-reference.md](cli-reference.md) — every `sscp` command
- [troubleshooting.md](troubleshooting.md) — common problems and their fixes
