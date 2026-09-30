// Builds and artifact registration.
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Common;

namespace Sscp.ControlPlane.Application.Builds;

public sealed record RegisterArtifactCommand(Guid BuildId, long PipelineRunId, string Deployable, string Repository, string Digest);

public sealed class BuildService(IControlPlaneStore store, IApplicationCatalog applications, TimeProvider clock)
{
    public async Task<Outcome<Build>> RequestAsync(string application, string commit, string gitRef, BuildKind kind, int? pullRequest, string actor, CancellationToken cancellationToken)
    {
        var registered = applications.Find(application);
        if (registered is null)
        {
            return DomainError.NotFound("application.unknown", $"Application '{application}' is not registered.");
        }

        // Deployment changes are pull requests on the application's GitOps repository.
        var repository = kind == BuildKind.DeploymentChange ? registered.GitOps?.Repository : registered.SourceRepository;
        if (repository is null)
        {
            return DomainError.Validation("application.gitops.unknown", $"Application '{application}' has no GitOps repository.");
        }

        var build = Build.Request(application, repository, commit, gitRef, kind, pullRequest, clock.GetUtcNow());
        if (!build.Succeeded)
        {
            return build;
        }

        store.Add(build.Value);
        store.Audit(actor, "build.requested", "build", build.Value.Id.ToString(), new { application, commit, gitRef, kind = kind.ToString(), pullRequest });
        await store.SaveChangesAsync(cancellationToken);
        return build;
    }

    public async Task<Outcome> AttachRunAsync(Guid buildId, long runId, string actor, CancellationToken cancellationToken)
    {
        var build = await store.BuildAsync(buildId, cancellationToken);
        if (build is null)
        {
            return DomainError.NotFound("build.unknown", "Build not found.");
        }

        var attached = build.AttachRun(runId, clock.GetUtcNow());
        if (attached.Succeeded)
        {
            store.Audit(actor, "build.run-attached", "build", buildId.ToString(), new { runId });
            await store.SaveChangesAsync(cancellationToken);
        }

        return attached;
    }

    public async Task<Outcome> CompleteAsync(Guid buildId, long? runId, bool succeeded, string? reason, Caller caller, CancellationToken cancellationToken)
    {
        var build = await store.BuildAsync(buildId, cancellationToken);
        if (build is null)
        {
            return DomainError.NotFound("build.unknown", "Build not found.");
        }

        if (!RunBinding.Allows(caller, runId, build.AcceptsRun))
        {
            return DomainError.Forbidden("build.run.not-dispatched", "Only the pipeline run dispatched for this build can complete it.");
        }

        var completed = build.Complete(succeeded, reason, clock.GetUtcNow());
        if (completed.Succeeded)
        {
            store.Audit(caller.Identity, succeeded ? "build.succeeded" : "build.failed", "build", buildId.ToString(), new { reason, runId });
            await store.SaveChangesAsync(cancellationToken);
            var tags = new[] { ControlPlaneMetrics.Tag("kind", build.Kind.ToString()), ControlPlaneMetrics.Tag("status", succeeded ? "succeeded" : "failed") };
            ControlPlaneMetrics.BuildsCompleted.Add(1, tags);
            ControlPlaneMetrics.BuildDuration.Record((build.UpdatedAt - build.CreatedAt).TotalSeconds, tags);
        }

        return completed;
    }

    // Called by the build zone after pushing an image to the candidate location.
    public async Task<Outcome<Artifact>> RegisterArtifactAsync(RegisterArtifactCommand command, Caller caller, CancellationToken cancellationToken)
    {
        if (!ZonePermissions.MayRegisterArtifacts(caller.Zone))
        {
            return DomainError.Forbidden("artifact.register.zone", "Only the build zone registers artifacts.");
        }

        var build = await store.BuildAsync(command.BuildId, cancellationToken);
        if (build is null)
        {
            return DomainError.NotFound("build.unknown", "Build not found.");
        }

        if (build.Kind != BuildKind.MainBranch || !build.AcceptsRun(command.PipelineRunId))
        {
            return DomainError.Forbidden("build.run.not-dispatched", "Artifacts are accepted only from the pipeline run dispatched for this main build.");
        }

        var application = applications.Find(build.Application)!;
        if (!application.Deployables.Contains(command.Deployable))
        {
            return DomainError.Validation("artifact.deployable.unknown", $"'{command.Deployable}' is not a registered deployable of {build.Application}.");
        }

        var reference = ArtifactReference.Create(command.Repository, command.Digest);
        if (!reference.Succeeded)
        {
            return reference.Error!;
        }

        if (!reference.Value.Repository.StartsWith(application.CandidateRepositoryPrefix + "/", StringComparison.Ordinal))
        {
            return DomainError.Validation("artifact.repository.not-candidate", $"Candidates must be pushed under {application.CandidateRepositoryPrefix}.");
        }

        var existing = (await store.ArtifactsForBuildAsync(build.Id, cancellationToken)).FirstOrDefault(a => a.Deployable == command.Deployable);
        if (existing is not null)
        {
            // Same build, same deployable: the digest must not change (build once, never rebuild).
            return existing.Digest == reference.Value.Digest.Value
                ? existing
                : DomainError.Conflict("artifact.digest.changed", $"{command.Deployable} was already registered for this build with digest {existing.Digest}.");
        }

        var artifact = Artifact.Register(build.Id, build.Application, command.Deployable, build.Commit, reference.Value, clock.GetUtcNow());
        store.Add(artifact);
        store.Audit(caller.Identity, "artifact.registered", "artifact", artifact.Id.ToString(),
            new { reference = artifact.CandidateReference, build = build.Id, commit = build.Commit, runId = command.PipelineRunId });
        await store.SaveChangesAsync(cancellationToken);
        return artifact;
    }
}
