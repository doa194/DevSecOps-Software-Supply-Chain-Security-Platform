// Turns verified source events into builds and releases, starts the platform-owned
// pipelines for them, and reports results back as commit statuses.
//
// The Control Plane is the only thing that starts security, build and release pipelines,
// and it binds each one to the exact run it started. The results a developer sees on a
// pull request or commit ("sscp/source-security", "sscp/trust-decision", "sscp/release")
// are set by the Control Plane from its own decisions, never by the pipeline.
using Microsoft.Extensions.Logging;
using Sscp.ControlPlane.Application.Builds;
using Sscp.ControlPlane.Application.Releases;
using Sscp.ControlPlane.Application.Trust;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Policy;
using Sscp.ControlPlane.Domain.Releases;

namespace Sscp.ControlPlane.Application.Orchestration;

public sealed partial class OrchestrationService(
    IControlPlaneStore store,
    IApplicationCatalog applications,
    BuildService builds,
    ReleaseService releases,
    IPipelinePlatform platform,
    ILogger<OrchestrationService> logger)
{
    public static readonly Caller Self = new("service:controlplane", CallerZone.ControlPlane);

    public Task HandleAsync(SourceEvent sourceEvent, CancellationToken cancellationToken) => sourceEvent switch
    {
        PushedToMain push => OnPushToMainAsync(push, cancellationToken),
        PullRequestUpdated pullRequest => OnPullRequestAsync(pullRequest, cancellationToken),
        TagPushed tag => OnTagAsync(tag, cancellationToken),
        _ => Task.CompletedTask,
    };

    private async Task OnPushToMainAsync(PushedToMain push, CancellationToken cancellationToken)
    {
        var application = applications.FindBySourceRepository(push.Repository);
        if (application is null || await store.LatestBuildForCommitAsync(application.Name, push.Commit, BuildKind.MainBranch, cancellationToken) is not null)
        {
            return;
        }

        var build = await builds.RequestAsync(application.Name, push.Commit, "refs/heads/main", BuildKind.MainBranch, null, $"gitea:{push.Actor}", cancellationToken);
        if (build.Succeeded)
        {
            await StartBuildPipelineAsync(build.Value, Workflows.Main, StatusContexts.TrustDecision, new Dictionary<string, string>(), cancellationToken);
        }
    }

    // A pull request on an application repository gets the source-security gate; one on
    // the application's GitOps repository gets the deployment-security gate.
    private async Task OnPullRequestAsync(PullRequestUpdated pullRequest, CancellationToken cancellationToken)
    {
        var (application, kind) = applications.FindBySourceRepository(pullRequest.Repository) is { } source
            ? (source, BuildKind.PullRequest)
            : (applications.FindByGitOpsRepository(pullRequest.Repository), BuildKind.DeploymentChange);
        if (application is null || await store.LatestBuildForCommitAsync(application.Name, pullRequest.HeadCommit, kind, cancellationToken) is not null)
        {
            return;
        }

        var build = await builds.RequestAsync(application.Name, pullRequest.HeadCommit, $"refs/pull/{pullRequest.Number}/head", kind,
            pullRequest.Number, $"gitea:{pullRequest.Actor}", cancellationToken);
        if (build.Succeeded)
        {
            var (workflow, context) = PipelineFor(kind);
            await StartBuildPipelineAsync(build.Value, workflow, context,
                new Dictionary<string, string> { ["pull_request"] = pullRequest.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken);
        }
    }

    // Which platform pipeline checks a build of each kind, and the commit status it sets.
    private static (string Workflow, string Context) PipelineFor(BuildKind kind) => kind switch
    {
        BuildKind.PullRequest => (Workflows.PullRequest, StatusContexts.SourceSecurity),
        BuildKind.DeploymentChange => (Workflows.Deployment, StatusContexts.DeploymentSecurity),
        _ => (Workflows.Main, StatusContexts.TrustDecision),
    };

    // An operator re-runs the pipeline for a commit whose previous run failed for reasons
    // unrelated to the code (for example a service was down). A new build is created and
    // dispatched; the failed build and its evidence stay on record.
    public async Task<Outcome<Build>> RetryBuildAsync(Guid buildId, string actor, CancellationToken cancellationToken)
    {
        var previous = await store.BuildAsync(buildId, cancellationToken);
        if (previous is null)
        {
            return DomainError.NotFound("build.unknown", "Build not found.");
        }

        if (previous.Status is BuildStatus.Requested or BuildStatus.Running)
        {
            return DomainError.Conflict("build.retry.running", "The build is still running.");
        }

        var build = await builds.RequestAsync(previous.Application, previous.Commit, previous.Ref, previous.Kind, previous.PullRequest, actor, cancellationToken);
        if (!build.Succeeded)
        {
            return build;
        }

        store.Audit(actor, "build.retried", "build", previous.Id.ToString(), new { retry = build.Value.Id });
        await store.SaveChangesAsync(cancellationToken);
        var (workflow, context) = PipelineFor(previous.Kind);
        var inputs = previous.PullRequest is { } number
            ? new Dictionary<string, string> { ["pull_request"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : new Dictionary<string, string>();
        await StartBuildPipelineAsync(build.Value, workflow, context, inputs, cancellationToken);
        return build;
    }

    private async Task StartBuildPipelineAsync(Build build, string workflow, string context, Dictionary<string, string> extraInputs, CancellationToken cancellationToken)
    {
        var inputs = new Dictionary<string, string>(extraInputs)
        {
            ["build_id"] = build.Id.ToString(),
            ["application"] = build.Application,
            ["repository"] = build.SourceRepository,
            ["commit"] = build.Commit,
        };
        try
        {
            var run = await platform.DispatchAsync(workflow, inputs, cancellationToken);
            await builds.AttachRunAsync(build.Id, run.RunId, Self.Identity, cancellationToken);
            await SetStatusAsync(build.SourceRepository, build.Commit, context, CommitState.Pending, "security pipeline running", run.Url, cancellationToken);
        }
        catch (PipelineDispatchException error)
        {
            LogDispatchFailed(logger, workflow, build.Id, error);
            await builds.CompleteAsync(build.Id, null, false, $"could not start {workflow}: {error.Message}", Self, cancellationToken);
            await SetStatusAsync(build.SourceRepository, build.Commit, context, CommitState.Error, "the security pipeline could not be started", null, cancellationToken);
        }
    }

    private async Task OnTagAsync(TagPushed tag, CancellationToken cancellationToken)
    {
        var application = applications.FindBySourceRepository(tag.Repository);
        if (application is null)
        {
            return;
        }

        var commit = await platform.ResolveTagCommitAsync(tag.Repository, tag.Tag, cancellationToken);
        if (commit is null)
        {
            return;
        }

        var release = await releases.RequestAsync(application.Name, tag.Tag, commit, $"gitea:{tag.Actor}", cancellationToken);
        if (!release.Succeeded)
        {
            // For example a tag on a commit that never passed a main build.
            await SetStatusAsync(tag.Repository, commit, StatusContexts.Release, CommitState.Failure, Truncate($"release refused: {release.Error!.Message}"), null, cancellationToken);
            return;
        }

        if (release.Value.PipelineRunId is not null)
        {
            return; // redelivered webhook for a release that is already running
        }

        try
        {
            var run = await platform.DispatchAsync(Workflows.Release, new Dictionary<string, string>
            {
                ["release_id"] = release.Value.Id.ToString(),
                ["application"] = application.Name,
                ["repository"] = tag.Repository,
                ["tag"] = tag.Tag,
                ["commit"] = commit,
            }, cancellationToken);
            await releases.AttachRunAsync(release.Value.Id, run.RunId, Self.Identity, cancellationToken);
            await SetStatusAsync(tag.Repository, commit, StatusContexts.Release, CommitState.Pending, $"release {tag.Tag} running", run.Url, cancellationToken);
        }
        catch (PipelineDispatchException error)
        {
            LogDispatchFailed(logger, Workflows.Release, release.Value.Id, error);
            await releases.FailAsync(release.Value.Id, null, $"could not start {Workflows.Release}: {error.Message}", Self, cancellationToken);
            await SetStatusAsync(tag.Repository, commit, StatusContexts.Release, CommitState.Error, "the release pipeline could not be started", null, cancellationToken);
        }
    }

    // ------------------------------------------------------------ decisions → commit statuses

    public async Task ReportSourceDecisionAsync(Guid buildId, Decision decision, CancellationToken cancellationToken)
    {
        if (await store.BuildAsync(buildId, cancellationToken) is { } build)
        {
            await SetStatusAsync(build.SourceRepository, build.Commit, PipelineFor(build.Kind).Context, StateOf(decision.Outcome),
                Describe(decision.Outcome, decision.Results), null, cancellationToken);
        }
    }

    public async Task ReportBuildDecisionAsync(Guid buildId, IReadOnlyList<ArtifactDecision> decisions, CancellationToken cancellationToken)
    {
        if (await store.BuildAsync(buildId, cancellationToken) is not { } build || decisions.Count == 0)
        {
            return;
        }

        var outcome = Combine(decisions.Select(d => d.Decision.Outcome));
        var failing = decisions.Where(d => d.Decision.Outcome == DecisionOutcome.Fail).Select(d => d.Deployable).ToList();
        var description = failing.Count == 0 ? $"{Name(outcome)} for {decisions.Count} images" : $"FAIL: {string.Join(", ", failing)}";
        await SetStatusAsync(build.SourceRepository, build.Commit, StatusContexts.TrustDecision, StateOf(outcome), Truncate(description), null, cancellationToken);
    }

    // A build that finished without success and without a decision must not leave its
    // required status pending forever.
    public async Task ReportBuildFailureAsync(Guid buildId, string? reason, CancellationToken cancellationToken)
    {
        if (await store.BuildAsync(buildId, cancellationToken) is not { } build)
        {
            return;
        }

        await SetStatusAsync(build.SourceRepository, build.Commit, PipelineFor(build.Kind).Context, CommitState.Failure,
            Truncate($"pipeline failed: {reason ?? "see the run"}"), null, cancellationToken);
    }

    public async Task ReportReleaseDecisionAsync(ReleaseDecision decision, CancellationToken cancellationToken)
    {
        var application = applications.Find(decision.Release.Application);
        if (application is not null)
        {
            await SetStatusAsync(application.SourceRepository, decision.Release.Commit, StatusContexts.Release, StateOf(decision.Outcome),
                Truncate($"{Name(decision.Outcome)}: release {decision.Release.Tag} {(decision.Outcome == DecisionOutcome.Fail ? "rejected" : "approved for signing")}"),
                null, cancellationToken);
        }
    }

    // The tagged commit's `sscp/release` status names the environment once Argo CD reports
    // the release healthy with exactly the promoted images.
    public async Task ReportDeploymentAsync(DeploymentReport report, CancellationToken cancellationToken)
    {
        var release = await store.ReleaseByGitOpsCommitAsync(report.Application, report.Revision, cancellationToken);
        if (release is { State: ReleaseState.Deployed } && applications.Find(release.Application) is { } application)
        {
            await SetStatusAsync(application.SourceRepository, release.Commit, StatusContexts.Release, CommitState.Success,
                Truncate($"release {release.Tag} deployed to {application.GitOps?.Environment ?? "the cluster"}"), null, cancellationToken);
        }
    }

    // ------------------------------------------------------------ run watcher

    // A pipeline run that ended without completing its build or release crashed, was
    // cancelled or timed out. Nothing may be left waiting: the build fails and the commit
    // status says so (fail closed).
    public async Task<int> ReconcileRunsAsync(CancellationToken cancellationToken)
    {
        var reconciled = 0;
        foreach (var build in await store.ActiveBuildsAsync(cancellationToken))
        {
            var ended = await FirstEndedRunAsync(build.PipelineRunIds, cancellationToken);
            if (ended is null)
            {
                continue;
            }

            var completed = await builds.CompleteAsync(build.Id, null, false, $"pipeline run {ended.Id} ended ({ended.Conclusion ?? "unknown"}) before the build was completed", Self, cancellationToken);
            if (completed.Succeeded)
            {
                reconciled++;
                await SetStatusAsync(build.SourceRepository, build.Commit, PipelineFor(build.Kind).Context, CommitState.Failure, $"pipeline ended without a decision ({ended.Conclusion})", ended.Url, cancellationToken);
            }
        }

        foreach (var release in await store.ActiveReleasesAsync(cancellationToken))
        {
            var ended = await FirstEndedRunAsync([release.PipelineRunId!.Value], cancellationToken);
            if (ended is null)
            {
                continue;
            }

            var failed = await releases.FailAsync(release.Id, null, $"pipeline run {ended.Id} ended ({ended.Conclusion ?? "unknown"}) while the release was {release.State}", Self, cancellationToken);
            if (failed.Succeeded && applications.Find(release.Application) is { } application)
            {
                reconciled++;
                await SetStatusAsync(application.SourceRepository, release.Commit, StatusContexts.Release, CommitState.Failure, $"release pipeline ended while {release.State}", ended.Url, cancellationToken);
            }
        }

        return reconciled;
    }

    private async Task<PipelineRun?> FirstEndedRunAsync(IEnumerable<long> runIds, CancellationToken cancellationToken)
    {
        foreach (var runId in runIds)
        {
            if (await platform.GetRunAsync(runId, cancellationToken) is { Completed: true } run)
            {
                return run;
            }
        }

        return null;
    }

    // ------------------------------------------------------------ helpers

    // A commit status that cannot be written must not undo a recorded decision; the
    // required check simply stays pending and the change cannot merge (fail closed).
    private async Task SetStatusAsync(string repository, string commit, string context, CommitState state, string description, string? url, CancellationToken cancellationToken)
    {
        try
        {
            await platform.SetCommitStatusAsync(repository, commit, context, state, description, url, cancellationToken);
        }
        catch (PipelineDispatchException error)
        {
            LogStatusFailed(logger, context, commit, error);
        }
    }

    private static DecisionOutcome Combine(IEnumerable<DecisionOutcome> outcomes)
    {
        var list = outcomes.ToList();
        return list.Contains(DecisionOutcome.Fail) ? DecisionOutcome.Fail
            : list.Contains(DecisionOutcome.PassWithException) ? DecisionOutcome.PassWithException
            : DecisionOutcome.Pass;
    }

    private static CommitState StateOf(DecisionOutcome outcome) => outcome == DecisionOutcome.Fail ? CommitState.Failure : CommitState.Success;

    private static string Name(DecisionOutcome outcome) => DecisionOutcomes.Name(outcome);

    private static string Describe(DecisionOutcome outcome, IReadOnlyList<RuleResult> results)
    {
        var blocking = results.Where(r => r.Blocking && !r.Passed).Select(r => r.Rule).ToList();
        return Truncate(blocking.Count == 0 ? Name(outcome) : $"FAIL: {string.Join(", ", blocking)}");
    }

    private static string Truncate(string text) => text.Length <= 140 ? text : string.Concat(text.AsSpan(0, 137), "...");

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not dispatch {Workflow} for {Subject}")]
    private static partial void LogDispatchFailed(ILogger logger, string workflow, Guid subject, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not set commit status {Context} on {Commit}")]
    private static partial void LogStatusFailed(ILogger logger, string context, string commit, Exception error);
}

// Raised by the pipeline platform adapter when Gitea refuses or cannot be reached.
public sealed class PipelineDispatchException(string message, Exception? inner = null) : Exception(message, inner);
