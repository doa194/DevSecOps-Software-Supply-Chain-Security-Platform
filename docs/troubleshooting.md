# Troubleshooting

This page gives practical fixes for problems seen while running the platform on a
workstation. Each entry lists the **symptom** you see, the usual **cause**, and the **fix**.

Unless stated otherwise:

- commands run from `supply-chain-platform/` in a POSIX shell (Git Bash on Windows);
- `kubectl` is given the platform's kubeconfig explicitly, as
  `--kubeconfig ../.local/generated/kubeconfig`.

## First steps

Two commands answer most questions:

```bash
uv run sscp status               # is every container running and healthy?
uv run sscp verify foundation    # TLS, networks, databases, Vault
```

To call the Control Plane API as a person, get a token first. The examples below reuse these
variables:

```bash
CP=https://localhost:7443
CA=../.local/pki/ca.crt
VICTOR=$(uv run python -c "from sscp.services import controlplane; print(controlplane.user_token('victor'))")
PAULA=$(uv run python -c "from sscp.services import controlplane; print(controlplane.user_token('paula'))")
```

`victor` can read everything; `paula` is the platform administrator. Tokens live five
minutes, so repeat the assignment if a call answers `401`.

## Quick index

| Symptom | Section |
|---|---|
| everything involving Vault fails after a restart | [After a Docker or host restart](#after-a-docker-or-host-restart) |
| `curl: (35)` or `(60) schannel: … revocation` on Windows | [curl fails with a Schannel revocation error](#curl-fails-with-a-schannel-revocation-error) |
| `sscp up` stops with a timeout from Gitea, Keycloak or Harbor | [sscp up fails with a timeout](#sscp-up-fails-with-a-timeout) |
| pipelines never start | [The CI runners do not come online](#the-ci-runners-do-not-come-online) |
| a pull request's `sscp/*` status stays pending | [A required status never appears](#a-required-status-never-appears) |
| a main build failed | [A main build fails part-way](#a-main-build-fails-part-way) |
| a build or release was refused and you need the reason | [Reading why trust was refused](#reading-why-trust-was-refused) |
| Argo CD out of sync, pods refused | [Argo CD shows an application OutOfSync](#argo-cd-shows-an-application-outofsync) |
| `ImagePullBackOff` | [An image will not pull](#an-image-will-not-pull) |
| an `ExternalSecret` is not ready | [Secrets are not delivered into the cluster](#secrets-are-not-delivered-into-the-cluster) |
| observability pods not ready | [An observability pod is Running but never Ready](#an-observability-pod-is-running-but-never-ready) |
| random timeouts everywhere | [The workstation is slow and things time out](#the-workstation-is-slow-and-things-time-out) |

## After a Docker or host restart

**Symptom.** Signing, secret delivery and CI jobs fail. The Control Plane logs Vault errors.
`sscp verify` cannot get tokens.

**Cause.** Containers restart automatically, but Vault comes back **sealed**. A sealed Vault
means no signing grants, no External Secrets refresh and no CI zone logins. This is Vault's
design: only the stored unseal keys open it.

**Fix.** Re-run the idempotent bootstrap, which unseals Vault and re-applies the
configuration:

```bash
uv run sscp up --with quality,ci,cluster
```

Check:

```bash
curl -s --cacert "$CA" --ssl-no-revoke https://localhost:8200/v1/sys/seal-status
```

`"sealed":false` means Vault is back. A pipeline that was running during the restart is
marked failed by the Control Plane; [retry it](#a-main-build-fails-part-way).

## curl fails with a Schannel revocation error

**Symptom.** On Windows, `curl` with `--cacert` fails with
`schannel: … the revocation status is unknown`.

**Cause.** Windows' built-in curl uses Schannel, which tries to check whether the
certificate was revoked. The platform's local CA publishes no revocation list, so the check
cannot complete.

**Fix.** Add `--ssl-no-revoke`, as every example in this documentation does. Other curl
builds accept the option and ignore it. Do not use `-k`, which switches off certificate
checking altogether.

## sscp up fails with a timeout

**Symptom.** `sscp up` stops with `ReadTimeout` or `did not become healthy` while talking to
Gitea, Keycloak or Harbor, typically right after a restart.

**Cause.** The service is still starting. On a slow disk, Gitea and Harbor can take minutes
after a cold start.

**Fix.** Run the same `sscp up` command again. Every step is idempotent, and completed steps
are skipped quickly.

## The CI runners do not come online

**Symptom.** `sscp up --with ci` reports `runners not online after …`, or a pipeline never
starts.

**Cause.** Each runner starts a private, rootless Docker daemon before it registers. On a
slow disk that daemon can take longer than usual after a host restart.

**Fix.** The platform already gives the daemon a longer start-up wait
(`platform/runner/docker-run`), so re-running `sscp up --with ci` usually resolves it. If one
runner is stuck, restart it:

```bash
docker restart sscp-runner-build-1     # or sscp-runner-security-1, -trust-1, -validation-1
```

Confirm that the runners registered at the right scope:

```bash
uv run sscp verify ci-isolation
```

## A required status never appears

**Symptom.** A pull request shows `sscp/source-security` (or `sscp/deployment-security`) as
pending forever, or not at all.

**Causes.**

- The Control Plane was down when Gitea sent the webhook. Gitea does not re-send it.
- The security runner is offline, so the dispatched run waits for a runner.

**Fix.**

1. Check that the Control Plane and the runners are running: `uv run sscp status`.
2. Push a new commit to the pull request's branch. Gitea sends a new webhook, and the
   Control Plane dispatches a new pipeline run.
3. Watch the run in Gitea: `platform/supply-chain-platform` → **Actions**.

## A main build fails part-way

**Symptom.** A `main-pipeline` run failed. The commit shows no `sscp/trust-decision`
status, or the status says the build failed.

**First, find out why.** Open the run in Gitea (`platform/supply-chain-platform` →
**Actions** → the `main-pipeline` run) and read the failed job's log. List recent builds
with the failure reason the Control Plane recorded:

```bash
curl -s --cacert "$CA" --ssl-no-revoke -H "Authorization: Bearer $VICTOR" "$CP/api/builds?limit=5"
```

Each build shows its `status` and `failureReason`, for example
`source-security=success build=failure …`.

**If the failure was infrastructure**, retry the build as a new build. Examples are a
restart during the run, a registry that was briefly unreachable, or a job timeout. Retrying
needs the platform administrator (`paula`):

```bash
curl -s --cacert "$CA" --ssl-no-revoke -X POST -H "Authorization: Bearer $PAULA" "$CP/api/builds/<build-id>/retry"
```

The answer is `202 Accepted` with the new build. The old build stays on record.

**If the decision was a genuine `FAIL`**, fix the code or accept the risk formally. A retry
fails the same way. See [Reading why trust was refused](#reading-why-trust-was-refused) and
[risk-exceptions.md](risk-exceptions.md).

**A note on `409 concurrency.conflict`.** Two jobs of the same build sometimes update one
image's record at the same moment. The Control Plane refuses the later update without
storing it, and the CI helper retries it automatically, up to five times. You see this only
as a `409` line in the job log. The job fails only if every attempt conflicts, and a retry
of the build is then safe.

## Reading why trust was refused

A decision lists the rules that failed and the findings responsible. The trace of a digest
shows every decision about it:

```bash
DIGEST=sha256:<digest>
curl -s --cacert "$CA" --ssl-no-revoke -H "Authorization: Bearer $VICTOR" "$CP/api/artifacts/$DIGEST/trace"
```

In each entry of `decisions`, `blocking` lists the failed rules (for example
`vulnerabilities`, with the fingerprints of the findings), and `evidenceUsed` names the
reports. Download a raw report with `GET /api/evidence/{id}/report`.

The digests of a build's images are in `GET /api/builds/{id}` (`artifacts[].digest`). What
each rule means is explained in [trust-policy.md](trust-policy.md#decision-rules).

## Argo CD shows an application OutOfSync

**Symptom.** `platform-cluster` or `commerce` is `OutOfSync` or `Degraded`, a pod is
refused, or `argocd-repo-server` restarts repeatedly.

**See what Argo CD says:**

```bash
kubectl --kubeconfig ../.local/generated/kubeconfig -n argocd get applications
kubectl --kubeconfig ../.local/generated/kubeconfig -n argocd get application commerce -o jsonpath="{.status.operationState.message}"
```

**Causes and fixes.**

- **Admission refused a resource.** The message names the Kyverno policy and rule, for
  example `image is not signed with the commerce release key`. This is the platform working
  as intended. Fix the manifest, or release a properly signed image.
- **Superseded ConfigMaps.** Observability configuration is delivered as ConfigMaps whose
  names carry a content hash. Old copies are left behind on purpose, and they carry Argo
  CD's `IgnoreExtraneous` option, so they should not show as OutOfSync. If they do, delete
  the old ones:

  ```bash
  kubectl --kubeconfig ../.local/generated/kubeconfig -n observability get configmaps
  kubectl --kubeconfig ../.local/generated/kubeconfig -n observability delete configmap <old-hash-name>
  ```

- **repo-server restarts under disk load.** Its health check already has extra time
  (`cluster/helm/argocd.yaml`). If it still restarts, the disk is saturated. Wait for the
  current pipeline to finish (see the last section), then force a fresh comparison:

  ```bash
  kubectl --kubeconfig ../.local/generated/kubeconfig -n argocd annotate application platform-cluster argocd.argoproj.io/refresh=hard --overwrite
  ```

## An image will not pull

**Symptom.** A pod is stuck in `ImagePullBackOff`. The event mentions
`harbor.sscp.test:8443` and a `502` or an unexpected digest.

**Cause.** Harbor is still starting or was restarting, so its registry was briefly
unavailable.

**Fix.** Wait until every Harbor component is healthy, then let Kubernetes retry:

```bash
uv run sscp up --with ci      # brings Harbor fully up
uv run sscp verify registry   # confirms robots and the mirror
```

If a pod stays stuck after Harbor is healthy, delete it so the pull starts again:

```bash
kubectl --kubeconfig ../.local/generated/kubeconfig -n commerce-data delete pod <name>
```

## Secrets are not delivered into the cluster

**Symptom.** An `ExternalSecret` shows `SecretSyncedError`. A workload cannot start
because a Secret is missing.

**Causes.** Vault is sealed ([see above](#after-a-docker-or-host-restart)), or the workload
secrets were never stored in Vault.

**Fix.**

```bash
uv run sscp up --with cluster   # unseals Vault and stores workload and platform secrets
kubectl --kubeconfig ../.local/generated/kubeconfig -n commerce get externalsecrets
```

A running pod keeps its last delivered Secret, so existing workloads keep working while
delivery is briefly down.

## An observability pod is Running but never Ready

**Symptom.** `otel-collector`, `grafana`, `prometheus` or `loki` stays at `0/1`.

**Fixes.**

- **The collector is stuck after a node or Docker restart.** Delete the pod; the new one
  starts cleanly:

  ```bash
  kubectl --kubeconfig ../.local/generated/kubeconfig -n observability delete pod -l app.kubernetes.io/name=otel-collector
  ```

- **Grafana starts slowly.** Grafana rebuilds its in-memory database on each start, and its
  start-up probe allows for this. On a very slow disk, give it a few minutes before treating
  it as failed.

Once a release is deployed, check the whole stack:

```bash
uv run sscp verify observability
```

## The workstation is slow and things time out

**Symptom.** Verification checks fail with connection or handshake timeouts. Services
freeze for tens of seconds.

**Cause.** On a hard disk, a running pipeline saturates disk I/O and briefly stalls every
service. A full .NET build is the usual culprit. This is the most common cause of flaky
behaviour on a constrained machine.

**Fixes.**

- **Do not run verification suites while a pipeline is running.** Wait for the
  `main-pipeline` run to finish first.
- **Give Docker more memory if you can.** 12 GiB is the minimum and 16 GiB is recommended
  (`uv run sscp doctor` shows what Docker has). On Windows, set it in
  `%USERPROFILE%\.wslconfig`, then run `wsl --shutdown`.
- **Start only the capabilities you need.** Leave out `--with quality` when you are not
  running main builds.

The disk-friendly settings the platform already applies, and what production would change,
are listed in
[production-considerations.md](production-considerations.md#settings-for-a-slow-workstation-disk).

## Nothing else worked: reset and rebuild

The workspace is the source of truth: no needed state lives only in Docker. A clean rebuild:

```bash
uv run sscp reset --yes
uv run sscp up --with quality,ci,cluster
uv run sscp repo build
```

`reset` deletes `.local/`, including the Vault unseal keys, so anything signed with the old
key no longer verifies. See [failure-and-recovery.md](failure-and-recovery.md) for the full
recovery model.
