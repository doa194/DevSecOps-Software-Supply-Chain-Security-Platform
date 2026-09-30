// Releases: requested by a protected tag, re-evaluated by the trust zone right before
// signing, then signatures, promotions, the GitOps change and, from Argo CD's report, the
// deployment are recorded.
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Application.Trust;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Policy;
using Sscp.ControlPlane.Domain.Records;
using Sscp.ControlPlane.Domain.Releases;

namespace Sscp.ControlPlane.Application.Releases;

public sealed record ReleaseDecision(Release Release, DecisionOutcome Outcome, IReadOnlyList<ArtifactDecision> Artifacts);

public sealed record SignatureCommand(Guid ArtifactId, string KeyReference, string SignatureReference, IReadOnlyList<string> AttestationReferences, long PipelineRunId);

public sealed record PromotionCommand(Guid ArtifactId, string TrustedRepository, long PipelineRunId);

// What Argo CD reports after a sync: which GitOps revision runs and whether it is synced
// and healthy. The images it lists are recorded for information only; which images a
// revision runs is read from Git (IDesiredStateReader).
public sealed record DeploymentReport(string Application, string Revision, string SyncStatus, string Health, IReadOnlyList<string> Images);

public enum DeploymentResult
{
    Deployed,
    AlreadyDeployed,
    Unmatched,
    Mismatch,
}

public sealed class ReleaseService(
    IControlPlaneStore store,
    IApplicationCatalog applications,
    TrustService trust,
    IDesiredStateReader desiredState,
    TimeProvider clock)
{
    public async Task<Outcome<Release>> RequestAsync(string application, string tag, string commit, string requestedBy, CancellationToken cancellationToken)
    {
        var registered = applications.Find(application);
        if (registered is null)
        {
            return DomainError.NotFound("application.unknown", $"Application '{application}' is not registered.");
        }

        if (await store.ReleaseByTagAsync(application, tag, cancellationToken) is { } existing)
        {
            return existing;
        }

        var build = await store.LatestBuildForCommitAsync(application, commit, BuildKind.MainBranch, cancellationToken);
        if (build is null || build.Status != BuildStatus.Succeeded)
        {
            return DomainError.Conflict("release.build.missing", $"Commit {commit} has no successful main build; only built and scanned commits can be released.");
        }

        var artifacts = await store.ArtifactsForBuildAsync(build.Id, cancellationToken);
        var missing = registered.Deployables.Except(artifacts.Select(a => a.Deployable)).ToList();
        if (missing.Count > 0)
        {
            return DomainError.Conflict("release.artifacts.incomplete", $"The build has no artifact for: {string.Join(", ", missing)}.");
        }

        var release = Release.Request(application, tag, commit, build.Id, artifacts.Select(a => a.Id), requestedBy, clock.GetUtcNow());
        if (!release.Succeeded)
        {
            return release;
        }

        store.Add(release.Value);
        store.Audit(requestedBy, "release.requested", "release", release.Value.Id.ToString(), new { application, tag, commit, build = build.Id });
        await store.SaveChangesAsync(cancellationToken);
        return release;
    }

    public async Task<Outcome> AttachRunAsync(Guid releaseId, long runId, string actor, CancellationToken cancellationToken)
    {
        var release = await store.ReleaseAsync(releaseId, cancellationToken);
        if (release is null)
        {
            return DomainError.NotFound("release.unknown", "Release not found.");
        }

        var attached = release.AttachRun(runId, clock.GetUtcNow());
        if (attached.Succeeded)
        {
            store.Audit(actor, "release.run-attached", "release", releaseId.ToString(), new { runId });
            await store.SaveChangesAsync(cancellationToken);
        }

        return attached;
    }

    // The trust zone asks for this immediately before signing: time has passed since the
    // main build, so exceptions may have expired and SLA windows may have closed.
    public async Task<Outcome<ReleaseDecision>> EvaluateAsync(Guid releaseId, long runId, Caller caller, CancellationToken cancellationToken)
    {
        var (loaded, error) = await LoadForTrustRunAsync(releaseId, runId, caller, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var release = loaded!;
        var artifacts = await LoadArtifactsAsync(release, cancellationToken);
        var decisions = await trust.EvaluateArtifactsAsync(artifacts, release.Id, caller.Identity, cancellationToken);
        var outcome = decisions.Any(d => d.Decision.Outcome == DecisionOutcome.Fail) ? DecisionOutcome.Fail
            : decisions.Any(d => d.Decision.Outcome == DecisionOutcome.PassWithException) ? DecisionOutcome.PassWithException
            : DecisionOutcome.Pass;

        release.ApplyDecision(outcome, caller.Identity, clock.GetUtcNow());
        AuditChanges(release);
        await store.SaveChangesAsync(cancellationToken);
        return new ReleaseDecision(release, outcome, decisions);
    }

    public async Task<Outcome> RecordSignatureAsync(Guid releaseId, SignatureCommand command, Caller caller, CancellationToken cancellationToken)
    {
        var (release, artifact, error) = await LoadForTrustZoneAsync(releaseId, command.ArtifactId, command.PipelineRunId, caller, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        if (!release!.MaySign)
        {
            return DomainError.Conflict("release.not-approved", $"Release is {release.State}; only approved releases may be signed.");
        }

        var signed = artifact!.MarkSigned(caller.Identity, clock.GetUtcNow());
        if (!signed.Succeeded)
        {
            return signed;
        }

        store.Add(new SignatureRecord
        {
            Id = Guid.CreateVersion7(), ArtifactId = artifact.Id, ReleaseId = release.Id, Digest = artifact.Digest, KeyReference = command.KeyReference,
            SignatureReference = command.SignatureReference, AttestationReferences = command.AttestationReferences,
            PipelineRunId = command.PipelineRunId, SignedAt = clock.GetUtcNow(),
        });
        store.Audit(caller.Identity, "artifact.signed", "artifact", artifact.Id.ToString(), new { release = release.Id, digest = artifact.Digest, key = command.KeyReference, signature = command.SignatureReference });
        ControlPlaneMetrics.Signatures.Add(1);

        // The release is signed when each of its artifacts has a signature recorded for this
        // release. Artifact states cannot tell: an earlier release of the same build may have
        // signed and promoted them already.
        if (await AllRecordedAsync(release, artifact.Id, id => store.SignaturesForArtifactAsync(id, cancellationToken), s => s.ReleaseId))
        {
            release.MarkSigned(caller.Identity, clock.GetUtcNow());
            AuditChanges(release);
        }

        await store.SaveChangesAsync(cancellationToken);
        return Outcome.Ok();
    }

    public async Task<Outcome> RecordPromotionAsync(Guid releaseId, PromotionCommand command, Caller caller, CancellationToken cancellationToken)
    {
        var (release, artifact, error) = await LoadForTrustZoneAsync(releaseId, command.ArtifactId, command.PipelineRunId, caller, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var application = applications.Find(release!.Application)!;
        if (!command.TrustedRepository.StartsWith(application.TrustedRepositoryPrefix + "/", StringComparison.Ordinal))
        {
            return DomainError.Validation("promotion.repository.invalid", $"Trusted artifacts live under {application.TrustedRepositoryPrefix}.");
        }

        var promoted = artifact!.MarkPromoted(command.TrustedRepository, caller.Identity, clock.GetUtcNow());
        if (!promoted.Succeeded)
        {
            return promoted;
        }

        store.Add(new PromotionRecord
        {
            Id = Guid.CreateVersion7(), ArtifactId = artifact.Id, ReleaseId = release.Id, From = artifact.CandidateReference,
            To = $"{command.TrustedRepository}@{artifact.Digest}", PipelineRunId = command.PipelineRunId, PromotedAt = clock.GetUtcNow(),
        });
        store.Audit(caller.Identity, "artifact.promoted", "artifact", artifact.Id.ToString(), new { release = release.Id, to = $"{command.TrustedRepository}@{artifact.Digest}" });
        ControlPlaneMetrics.Promotions.Add(1);

        if (await AllRecordedAsync(release, artifact.Id, id => store.PromotionsForArtifactAsync(id, cancellationToken), p => p.ReleaseId))
        {
            release.MarkPromoted(caller.Identity, clock.GetUtcNow());
            AuditChanges(release);
        }

        await store.SaveChangesAsync(cancellationToken);
        return Outcome.Ok();
    }

    public async Task<Outcome> RecordGitOpsChangeAsync(Guid releaseId, long runId, string gitOpsCommit, Caller caller, CancellationToken cancellationToken)
    {
        var (release, error) = await LoadForTrustRunAsync(releaseId, runId, caller, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var updated = release!.MarkGitOpsUpdated(gitOpsCommit, caller.Identity, clock.GetUtcNow());
        if (updated.Succeeded)
        {
            AuditChanges(release);
            store.Audit(caller.Identity, "gitops.updated", "release", release.Id.ToString(), new { commit = gitOpsCommit });
            await store.SaveChangesAsync(cancellationToken);
        }

        return updated;
    }

    // Argo CD reports each healthy sync of a new GitOps revision. The release that wrote
    // that revision becomes Deployed only if the revision, read from Git, pins exactly the
    // digests the Control Plane approved for the release. Anything else is recorded and
    // audited, never silently accepted.
    public async Task<Outcome<DeploymentResult>> RecordDeploymentAsync(DeploymentReport report, string actor, CancellationToken cancellationToken)
    {
        if (applications.Find(report.Application) is not { GitOps: { } gitOps } application)
        {
            return DomainError.NotFound("application.unknown", $"Application '{report.Application}' is not registered for GitOps deployment.");
        }

        if (!IsCommitId(report.Revision) || report.Images.Count > MaxReportedImages || report.Images.Any(i => i.Length > 300))
        {
            return DomainError.Validation("deployment.report.invalid", "A deployment report needs a 40-character Git revision and at most 100 image references.");
        }

        var now = clock.GetUtcNow();
        var release = await store.ReleaseByGitOpsCommitAsync(report.Application, report.Revision, cancellationToken);
        if (release is null)
        {
            // A GitOps change no release made, for example a reviewed configuration change.
            // If it still pins exactly the digests of the deployed release, it runs that
            // release: the record is linked to it, so the running revision stays traceable
            // from the digest. Anything else is only observed.
            var current = await store.LatestDeployedReleaseAsync(report.Application, cancellationToken);
            var runsCurrent = current is not null && IsSyncedAndHealthy(report) && (await CompareAsync(current)).Matches;
            Record(runsCurrent ? current : null);
            store.Audit(actor, "deployment.observed", "application", report.Application,
                new { report.Revision, report.Health, report.Images, release = runsCurrent ? current!.Id : (Guid?)null });
            return await FinishAsync(runsCurrent ? DeploymentResult.AlreadyDeployed : DeploymentResult.Unmatched);
        }

        Record(release);
        if (release.State == ReleaseState.Deployed)
        {
            return await FinishAsync(DeploymentResult.AlreadyDeployed);
        }

        var comparison = await CompareAsync(release);
        if (!comparison.Matches || !IsSyncedAndHealthy(report))
        {
            store.Audit(actor, "deployment.mismatch", "release", release.Id.ToString(), new
            {
                report.Revision, report.SyncStatus, report.Health, readable = comparison.Pinned is not null,
                missing = comparison.Expected.Except(comparison.Pinned ?? []).ToList(),
                unexpected = (comparison.Pinned ?? []).Except(comparison.Expected).ToList(),
            });
            return await FinishAsync(DeploymentResult.Mismatch);
        }

        var deployed = release.MarkDeployed(actor, now);
        if (!deployed.Succeeded)
        {
            return deployed.Error!;
        }

        foreach (var artifact in comparison.Artifacts)
        {
            artifact.MarkDeployed(actor, now);
        }

        AuditChanges(release);
        store.Audit(actor, "release.deployed", "release", release.Id.ToString(), new { report.Revision, gitOps.Environment, images = comparison.Expected });
        return await FinishAsync(DeploymentResult.Deployed);

        void Record(Release? runs) => store.Add(new DeploymentRecord
        {
            Id = Guid.CreateVersion7(), Application = report.Application, ReleaseId = runs?.Id, Environment = gitOps.Environment,
            GitOpsRevision = report.Revision, SyncStatus = report.SyncStatus, HealthStatus = report.Health, Images = report.Images, ObservedAt = now,
        });

        // Expected digests come from the Control Plane's own artifact records, not from
        // anything the trust zone reported; the pinned ones from the revision in Git.
        async Task<(bool Matches, IReadOnlyList<Artifact> Artifacts, HashSet<string> Expected, HashSet<string>? Pinned)> CompareAsync(Release candidate)
        {
            var artifacts = await LoadArtifactsAsync(candidate, cancellationToken);
            var expected = artifacts.Select(a => $"{application.TrustedRepositoryPrefix}/{a.Deployable}@{a.Digest}").ToHashSet(StringComparer.Ordinal);
            var pinned = (await desiredState.PinnedImagesAsync(gitOps, report.Revision, cancellationToken))?
                .Where(image => image.StartsWith(application.TrustedRepositoryPrefix + "/", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            return (pinned is not null && pinned.SetEquals(expected), artifacts, expected, pinned);
        }

        async Task<Outcome<DeploymentResult>> FinishAsync(DeploymentResult result)
        {
            await store.SaveChangesAsync(cancellationToken);
            ControlPlaneMetrics.Deployments.Add(1, new KeyValuePair<string, object?>("result", result.ToString().ToLowerInvariant()));
            return Outcome.Ok(result);
        }
    }

    private const int MaxReportedImages = 100;

    private static bool IsSyncedAndHealthy(DeploymentReport report) => report.SyncStatus == "Synced" && report.Health == "Healthy";

    private static bool IsCommitId(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    public async Task<Outcome> FailAsync(Guid releaseId, long? runId, string reason, Caller caller, CancellationToken cancellationToken)
    {
        var release = await store.ReleaseAsync(releaseId, cancellationToken);
        if (release is null)
        {
            return DomainError.NotFound("release.unknown", "Release not found.");
        }

        if (!RunBinding.Allows(caller, runId, release.AcceptsRun))
        {
            return DomainError.Forbidden("release.run.not-dispatched", "Only the pipeline run dispatched for this release can fail it.");
        }

        var failed = release.Fail(reason, caller.Identity, clock.GetUtcNow());
        if (failed.Succeeded)
        {
            AuditChanges(release);
            await store.SaveChangesAsync(cancellationToken);
        }

        return failed;
    }

    // Evaluation, signing, promotion and GitOps steps belong to the trust zone, and only to
    // the one run the Control Plane dispatched for this release.
    private async Task<(Release? Release, DomainError? Error)> LoadForTrustRunAsync(Guid releaseId, long runId, Caller caller, CancellationToken cancellationToken)
    {
        if (caller.Zone != CallerZone.Trust)
        {
            return (null, DomainError.Forbidden("release.zone", "Only the trust zone acts on releases."));
        }

        var release = await store.ReleaseAsync(releaseId, cancellationToken);
        if (release is null)
        {
            return (null, DomainError.NotFound("release.unknown", "Release not found."));
        }

        return release.AcceptsRun(runId)
            ? (release, null)
            : (null, DomainError.Forbidden("release.run.not-dispatched", "Only the pipeline run dispatched for this release can act on it."));
    }

    private async Task<(Release? Release, Artifact? Artifact, DomainError? Error)> LoadForTrustZoneAsync(Guid releaseId, Guid artifactId, long runId, Caller caller, CancellationToken cancellationToken)
    {
        var (release, error) = await LoadForTrustRunAsync(releaseId, runId, caller, cancellationToken);
        if (error is not null)
        {
            return (null, null, error);
        }

        if (!release!.ArtifactIds.Contains(artifactId))
        {
            return (null, null, DomainError.NotFound("release.artifact.unknown", "The artifact is not part of this release."));
        }

        var artifact = await store.ArtifactAsync(artifactId, cancellationToken);
        return (release, artifact, artifact is null ? DomainError.NotFound("artifact.unknown", "Artifact not found.") : null);
    }

    // True when every artifact of the release has a record (signature or promotion) for this
    // release. `current` is the artifact whose record is being added and not yet saved.
    private static async Task<bool> AllRecordedAsync<TRecord>(Release release, Guid current,
        Func<Guid, Task<IReadOnlyList<TRecord>>> recordsFor, Func<TRecord, Guid?> releaseOf)
    {
        foreach (var id in release.ArtifactIds.Where(id => id != current))
        {
            if (!(await recordsFor(id)).Any(record => releaseOf(record) == release.Id))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<IReadOnlyList<Artifact>> LoadArtifactsAsync(Release release, CancellationToken cancellationToken)
    {
        var artifacts = new List<Artifact>();
        foreach (var id in release.ArtifactIds)
        {
            artifacts.Add((await store.ArtifactAsync(id, cancellationToken))!);
        }

        return artifacts;
    }

    private void AuditChanges(Release release)
    {
        foreach (var change in release.PendingChanges)
        {
            store.Audit(change.Actor, "release.state-changed", "release", release.Id.ToString(), new { from = change.From.ToString(), to = change.To.ToString(), change.Reason });
        }

        release.ClearPendingChanges();
    }
}
