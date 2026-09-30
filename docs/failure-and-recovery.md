# Failure and recovery

The platform is built to **fail closed**. When a control that trust depends on is missing
or unavailable, the answer is "not trusted", never "trusted by default". This document
covers:

- the failure modes the platform handles, and what it does in each;
- which automated check proves each behaviour;
- how to recover after an outage.

Two rules run through all of it:

- **No silent skips.** A mandatory scanner that did not complete, or evidence the Control
  Plane cannot verify, makes the trust decision fail. Missing proof is never read as
  success.
- **No fallback path.** There is exactly one way for an image to become trusted: the
  Control Plane's decision plus a Vault-backed signature. There is exactly one way to reach
  the cluster: a GitOps commit that Argo CD pulls. When a step is down, the chain stops
  there. Nothing routes around it.

```mermaid
flowchart LR
  down{{A component is down}} --> stop[The chain stops at that step]
  stop --> nothing[Nothing is signed, promoted<br/>or deployed in its place]
  nothing --> recover[sscp up restores the service]
  recover --> retry[Retry the build or cut a new release tag]
```

## How each failure mode is handled

The **Proven by** column names the automated check:

- suite names are operational checks against the running platform
  (`uv run sscp verify <suite>`);
- `.NET` names are test classes in `supply-chain-platform/tests`;
- `commerce` names are test classes in `commerce-app/tests`.

| Failure | Platform behaviour | Proven by |
|---|---|---|
| A mandatory scanner does not complete | The zone reports the scanner as failed. The Control Plane counts a scanner failure and the trust decision fails. The gate cannot be passed by leaving a scan out. | `.NET` `TrustEvaluatorTests.A_scanner_that_did_not_complete_fails_closed`, `Missing_mandatory_evidence_fails` |
| The vulnerability database is older than 7 days | Vulnerability evidence from it is not accepted; the decision fails. | `.NET` `TrustEvaluatorTests.A_stale_vulnerability_database_fails`; `registry` suite: `test_offline_vulnerability_databases_are_fresh_enough_for_the_policy` |
| Evidence is forged, replayed or edited after upload | Evidence is bound to the build, the dispatched run and the image digest. Its stored report is re-hashed at every evaluation. Any mismatch blocks trust. | `.NET` `ZoneBoundaryTests.Evidence_from_a_run_the_control_plane_did_not_dispatch_is_refused`, `TrustFlowTests.Evidence_changed_in_storage_after_ingestion_blocks_trust` |
| A blocking vulnerability is present | The image's decision is `FAIL`. It is never signed, promoted or deployed. | `.NET` `TrustFlowTests.Critical_fixable_vulnerability_fails_and_the_release_cannot_be_signed` |
| An approved risk exception expires | The next evaluation no longer applies it, so the finding blocks again. The trust zone re-evaluates immediately before signing. | `.NET` `TrustFlowTests.Approved_exception_turns_the_failure_into_pass_with_exception_until_it_expires` |
| The security-test environment cannot start | Both dynamic evidence kinds are submitted as failed executions; the decision names them and fails. | the `dynamic-security` job ([dynamic-security-testing.md](dynamic-security-testing.md)); `.NET` `TrustEvaluatorTests.A_scanner_that_did_not_complete_fails_closed` |
| Two jobs update the same artifact at once | The Control Plane refuses the later update with `409 concurrency.conflict` and stores nothing. The CI helper sends it again, up to five times. | Python `supply-chain-platform/ci/tests/test_controlplane_retry.py` |
| A pipeline run ends without finishing its build or release | The Control Plane's run watcher marks the build or release failed, with the reason. | `.NET` `OrchestrationTests.Build_whose_pipeline_run_ended_without_completing_it_is_failed` |
| The Control Plane is unavailable | No webhook is handled and no decision is made, so no candidate can become trusted. Required commit statuses stay pending, so merges and releases wait. On restart the audit chain still verifies. | `recovery` suite: `test_control_plane_outage_fails_closed_and_recovers`; `controlplane` suite: `test_recovers_from_a_restart_with_the_audit_chain_intact` |
| Vault is unavailable or sealed | CI zones cannot log in. The Control Plane cannot issue a signing grant (`503 signing.unavailable`). External Secrets cannot refresh. Nothing is signed or promoted. On restart Vault is unsealed and its keys survive. | `recovery` suite: `test_vault_outage_fails_closed_and_recovers`; `.NET` `SigningTests.Vault_outage_is_reported_as_unavailable_and_leaves_the_release_approved`; `foundation` suite: `test_vault_recovers_from_a_restart_without_losing_state` |
| Harbor is unavailable | No image can be pushed or promoted, and the cluster cannot pull an image it does not already have. The release stops; nothing unverified is deployed. | recovery procedure below (not automated, see [Limitations](#limitations)) |
| Argo CD is unavailable | A committed GitOps change is not applied. No CI path deploys directly. When Argo CD returns, it reconciles to the committed state. | recovery procedure below |
| Kyverno is unavailable | The admission webhooks use `failurePolicy: Fail`, so the API server refuses the pods Kyverno cannot check. | `cluster` suite (policies are configured and enforced); [gitops-and-admission.md](gitops-and-admission.md#admission-policies) |
| An unsigned or tampered image reaches admission | Kyverno refuses the pod: the signature or a required attestation is missing, or does not match the digest. | `cluster` suite: `test_an_unsigned_image_pushed_into_the_trusted_project_is_refused`, `test_images_outside_the_trusted_project_or_by_tag_are_refused` |
| A deployment does not match its release | A running revision counts as deployed only when it pins exactly the approved digests. Otherwise it is recorded as a mismatch, and an alert fires. | `.NET` `DeploymentTests.A_revision_pinning_other_digests_than_the_release_approved_does_not_count_as_deployed`; `cluster` suite: `test_the_running_release_is_traceable_from_digest_to_cluster` |
| A worker cannot process a message | The consumer retries with an attempt counter, then dead-letters. The outbox and inbox keep publishing and handling idempotent. | `commerce` `OutboxInboxTests` |
| A forged or tampered integration event arrives | The consumer checks the signature, the publisher and the allowed event types, and dead-letters anything that fails. | `commerce` `EnvelopeSigningTests`; `OutboxInboxTests.A_tampered_message_is_dead_lettered_and_never_applied` |
| A service restarts | State lives in PostgreSQL, MinIO (object-locked evidence) and Vault. A restart re-reads it. The audit logs are hash-chained, so corruption is detectable. | `recovery`, `foundation` and `controlplane` suites |

How deliberate attacks are blocked, as opposed to failures, is mapped scenario by scenario
in [threat-model.md](threat-model.md#attack-scenarios-and-how-they-are-stopped).

## Recovering from an outage

`sscp up` is **idempotent**: it can be re-run safely. After most outages, re-running it
restores the affected service without touching the rest. All commands run from
`supply-chain-platform/`.

### Docker Desktop or the workstation restarted

A Docker or host restart brings the containers back, but Vault comes back **sealed**.
Signing, secret delivery and CI zone logins stop until it is unsealed.

```bash
uv run sscp up --with quality,ci,cluster
```

This unseals Vault with the stored keys and re-applies the configuration. Nothing else is
recreated. A main build that was running during the restart is marked failed by the
Control Plane's run watcher. Retry it as described in
[troubleshooting.md](troubleshooting.md#a-main-build-fails-part-way).

### The Control Plane is unhealthy or stopped

```bash
uv run sscp up
uv run sscp verify controlplane
```

The Control Plane starts against its existing database, and the audit chain still
verifies. Webhooks sent by Gitea while it was down are not replayed:

- re-trigger a pull request's checks by pushing a new commit;
- retry a main build as a platform administrator (`POST /api/builds/{id}/retry`);
- re-send a deployment report as described in
  [gitops-and-admission.md](gitops-and-admission.md#operating).

### Harbor is unavailable

While Harbor is down, no image can be promoted, and the cluster cannot pull an image it
does not already have. A release stops at promotion, and no unverified image is deployed.
Harbor is a group of containers. `sscp up` restarts them together and waits until they are
healthy:

```bash
uv run sscp up --with ci
uv run sscp verify registry
```

A release that failed at promotion is not partly trusted. The GitOps repository was not
changed, so nothing new was deployed. Cut the release again with a new tag once Harbor is
healthy.

### Argo CD is unavailable

While Argo CD is down, the cluster keeps running what it already has, but a new GitOps
commit is not applied. There is no path that deploys without it. Argo CD reconciles from
Git when it returns, so recovery means bringing it back:

```bash
uv run sscp up --with cluster
kubectl --kubeconfig ../.local/generated/kubeconfig -n argocd get applications
```

Both applications should return to `Synced` and `Healthy`. If an application is stuck, a
hard refresh makes Argo CD re-read Git:

```bash
kubectl --kubeconfig ../.local/generated/kubeconfig -n argocd annotate application commerce argocd.argoproj.io/refresh=hard --overwrite
```

### A release failed part-way

A release that fails after signing or promoting some images leaves signed, positively
decided copies in the trusted project. The GitOps repository is changed only by a
**successful** release, however, so nothing partial is ever deployed. Fix the cause, then
cut a new release tag. Release tags are immutable, so reusing one is refused. The stale
trusted copies are harmless and never referenced.

### Rebuilding from nothing

The workspace is the source of truth. A full reset and rebuild:

```bash
uv run sscp reset --yes
uv run sscp up --with quality,ci,cluster
uv run sscp repo build          # the first main build after a fresh start
uv run sscp repo tag -t v1.0.0  # release once the build has passed
```

A reset creates a new signing key, so nothing signed before the reset verifies afterwards.

## Verifying fail-closed behaviour and recovery

```bash
uv run sscp verify recovery
```

This stops the Control Plane and then Vault, one at a time:

- **Control Plane.** While it is stopped, its API does not answer, so no webhook can be
  handled and no decision obtained. After the restart, the API answers again and
  `GET /api/audit/verification` reports the audit chain intact.
- **Vault.** While it is stopped, no client can reach it, so no zone can log in and no
  signing grant can be issued. After the restart, Vault is sealed, as expected. The suite
  unseals it with the stored keys and checks that the Transit engine, which holds the
  signing key, survived.

Each check restores its service even if an assertion fails, so the suite never leaves the
platform down.

## Limitations

- **Only two services are stopped automatically.** The `recovery` suite covers the Control
  Plane and Vault, which restart quickly. Harbor and Argo CD outages are recovered with the
  documented commands instead. Restarting them repeatedly is slow and heavy on one
  workstation.
- **Missed webhooks are not replayed.** Events that arrive while the Control Plane is down
  must be re-triggered, as described above.
- **Recovery needs the Vault unseal keys** in `.local/secrets/vault-init.json`.
  `sscp reset` deletes them. Without them, Vault's data cannot be recovered and the platform
  must be rebuilt.
- **Backups are not implemented.** See
  [production-considerations.md](production-considerations.md) for what production would
  add.
