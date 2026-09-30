# `sscp` command reference

`sscp` is the platform's own command-line tool. It creates, configures, verifies and
removes the whole platform on a workstation, and it acts as the platform's people (alice,
max, rhea, pat, omar) when you want to make a change the way a team would.

It is a Python package (`supply-chain-platform/automation/src/sscp`) run through `uv`,
which installs its locked dependencies into an isolated environment on first use.
Run every command from the `supply-chain-platform` directory:

```bash
cd supply-chain-platform
uv run sscp <command> [options]
```

Every command prints each step it performs (`..` before, `ok` after with the time taken,
`FAIL` with the reason). Exit status `0` means success, `1` means a step failed, `2`
means the command was used incorrectly (for example a missing option).

## Summary

| Command | Purpose |
|---|---|
| [`doctor`](#doctor) | Check workstation prerequisites and the pinning policy |
| [`up`](#up) | Start and configure the platform (idempotent) |
| [`status`](#status) | Show the state of every platform container |
| [`down`](#down) | Stop everything, keeping all data |
| [`reset`](#reset) | Delete everything the platform created |
| [`verify`](#verify) | Run operational verification suites against the running platform |
| [`repo`](#repo) | Change the Gitea repositories as the platform's people would |
| [`tools`](#tools) | Maintain CI tool data (vulnerability databases) |
| [`workload`](#workload) | Run the commerce workload as local processes for development |

## `doctor`

```bash
uv run sscp doctor
```

Checks, before anything is started:

| Check | Blocking when |
|---|---|
| Docker CLI present and daemon reachable | always |
| Memory available to Docker | below 12 GiB (a warning below the recommended 16 GiB) |
| CPUs available to Docker | never (warning below 4) |
| Free disk space on the workspace drive | below 30 GiB (a warning below the recommended 50 GiB) |
| Python 3.12 or newer, git, kubectl, Helm | missing |
| .NET SDK, uv | never (warning if missing) |
| Every image in `versions.yaml` pinned by digest | any image unpinned |
| Every downloaded tool has a SHA-256 in `versions.yaml` | any tool without one |

## `up`

```bash
uv run sscp up [--with CAPABILITIES]
```

Starts the platform and converges it to its configured state. Always starts the core
services (PostgreSQL, Vault, Keycloak, MinIO, Gitea), Harbor and the Security Control
Plane. Safe to run repeatedly: it starts stopped containers, unseals Vault after a restart,
re-applies configuration, rebuilds images only when their inputs changed, and skips work
that is already done.

| Option | Meaning |
|---|---|
| `--with quality` | also SonarQube and the platform quality gate |
| `--with ci` | also the tool mirror, vulnerability databases, Harbor projects and robots, `ci-tools`, the four CI zone runners |
| `--with cluster` | also the kind cluster, its add-ons, Vault access for External Secrets and the platform's cluster state |
| `--with workload` | also PostgreSQL, Redis, RabbitMQ and MinIO for host development |

Capabilities combine with commas: `--with quality,ci,cluster`. The steps each capability
performs are listed in [setup-guide.md](setup-guide.md#what-sscp-up-does).

## `status`

```bash
uv run sscp status
```

Lists every container of the platform's Compose project (`sscp`) and of Harbor (`harbor`)
with its state, marked `ok` when running and healthy.

## `down`

```bash
uv run sscp down
```

Stops the kind node, Harbor and every platform service, including services started by
optional capabilities. All data (volumes, generated credentials, the cluster) is kept.
`sscp up` starts everything again.

## `reset`

```bash
uv run sscp reset --yes
```

Deletes the kind cluster, every platform container, volume and network, and the whole
`.local/` directory (certificates, credentials, Vault unseal keys, generated
configuration). Refuses to run without `--yes`. The next `sscp up` creates a completely new
installation; nothing signed before the reset verifies afterwards, because the signing key
is new.

## `verify`

```bash
uv run sscp verify [SUITE ...]
```

Runs operational verification suites — pytest modules in
`automation/tests/operational/` that observe the running platform. With no suite name, all
suites run.

| Suite | Checks |
|---|---|
| `foundation` | TLS, name resolution, network isolation of data services, database isolation, write-once evidence storage, Harbor health, Vault restart recovery |
| `controlplane` | token meaning, internal-only health and metrics, restricted database role, container hardening, restart recovery with an intact audit chain |
| `ci-isolation` | runner scopes, jobs without Docker or credentials, zone identities bound to their runners, private daemons, unsigned webhooks refused |
| `registry` | robot permissions, project visibility, vulnerability database age |
| `signing` | non-exportable key, grant rules, signatures of promoted images, immutable release tags |
| `cluster` | admission, network paths, identities, secret scoping, CI has no cluster access, Argo CD project limits, traceability |
| `observability` | scrape targets, alert rules, release identity on workload metrics and logs, vulnerability reports, dashboards |
| `recovery` | the Control Plane and Vault fail closed while stopped and recover |

Extra pytest arguments are not accepted through `sscp verify`; to run one check, call
pytest directly:

```bash
uv run python -m pytest -m operational automation/tests/operational/test_cluster.py -k debug_containers
```

See [testing-strategy.md](testing-strategy.md) for what each layer covers.

## `repo`

```bash
uv run sscp repo ACTION [options]
```

Changes the Gitea repositories through the same protections a team faces: pushes go
through branch protection, merges need reviews and required statuses, tags need the
release manager.

| Action | Does | Acts as (default) |
|---|---|---|
| `sync` | Commits the current `supply-chain-platform/` directory to `platform/supply-chain-platform` `main` | `pat` |
| `propose` | Pushes a workspace repository as a new branch on top of `main` and opens a pull request | `alice` (commerce-app) or `pat` (commerce-gitops) |
| `approve` | Approves pull request `-n` | `max` (commerce-app) or `omar` (commerce-gitops) |
| `merge` | Merges pull request `-n`; Gitea refuses unless approvals and required statuses are satisfied | `alice` (commerce-app) or `pat` (commerce-gitops) |
| `tag` | Pushes a release tag on `commerce/commerce-app` | `rhea` |
| `build` | Asks Gitea to re-send the push event for the tip of `commerce-app` `main`, so the Control Plane builds it (needed once after a fresh start) | — |

| Option | Used by | Meaning |
|---|---|---|
| `-m`, `--message` | `sync`, `propose` | Commit message and pull request title (default "Update from the workspace") |
| `-b`, `--branch` | `propose` | Branch to create (required) |
| `--repo` | `propose`, `approve`, `merge` | `commerce-app` (default) or `commerce-gitops` |
| `-n`, `--number` | `approve`, `merge` | Pull request number (required) |
| `-t`, `--tag` | `tag` | Tag name, for example `v1.2.3` (required; must be `v<major>.<minor>.<patch>`) |
| `--commit` | `tag` | Commit to tag (default: the tip of `main`) |
| `--as` | `propose`, `approve`, `merge`, `tag` | Act as another Gitea account |

Examples:

```bash
uv run sscp repo sync -m "Tighten the Semgrep rules"
uv run sscp repo propose -b feature/faster-search -m "Faster catalogue search"
uv run sscp repo approve -n 12
uv run sscp repo merge -n 12
uv run sscp repo tag -t v1.3.0
uv run sscp repo propose --repo commerce-gitops -b change/two-replicas -m "Two API replicas"
uv run sscp repo approve --repo commerce-gitops -n 3
uv run sscp repo merge --repo commerce-gitops -n 3
```

Notes:

- `propose` publishes the **workspace directory's current content** as one commit on top
  of `main`. For `commerce-gitops` it keeps the two files the release bot owns
  (`overlays/local/kustomization.yaml` and `release.yaml`) from `main`, so a proposal never
  reverts released digests.
- Nothing ever creates a `.git` directory in the workspace: publishing uses a temporary
  Git directory, and credentials travel through environment-based Git configuration, never
  on the command line.
- `tag` pushes with Git like a person would, so Gitea's tag protection decides whether the
  account may create it.

## `tools`

```bash
uv run sscp tools refresh-db
```

Copies the current Trivy and Grype vulnerability databases into Harbor
(`platform-tools/trivy-db:2`, `platform-tools/grype-db:v6`). The trust policy refuses
vulnerability evidence from a database older than seven days, so run this at least weekly;
`sscp up --with ci` also refreshes them. See
[artifact-registry.md](artifact-registry.md#vulnerability-data).

## `workload`

```bash
uv run sscp workload setup|start|stop|reset
```

Runs the six commerce services as local .NET processes against the `workload` data
services (start them with `uv run sscp up --with workload`) and the platform Keycloak.

| Action | Does |
|---|---|
| `setup` | applies the commerce realm, creates one database role per module and worker plus the schema owner, generates the publisher signing keys, builds the solution, runs the migrations |
| `start` | starts the six services (gateway on `http://localhost:5080`, API on `5100`, workers on `5101`–`5104`); logs go to `.runs/workload-dev/` |
| `stop` | stops them |
| `reset` | stops them, recreates the database, broker virtual host and cache, then runs `setup` again |

## Related commands inside the pipelines

The pipelines use a separate helper, `sscp-ci`, that runs inside CI job containers; it is
not meant to be run on the workstation. Its commands are listed in
[ci-pipelines.md](ci-pipelines.md#the-ci-helper-sscp-ci).
