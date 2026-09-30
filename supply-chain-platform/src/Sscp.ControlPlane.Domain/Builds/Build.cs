// One run of the platform pipeline for one commit of an application.
//
// A build is created by the Control Plane when Gitea reports a push to main (or a pull
// request). It records which pipeline runs the Control Plane dispatched for it; evidence and
// artifacts are accepted only from those runs, so a job from any other workflow run cannot
// attach evidence to this commit.
using System.Text.RegularExpressions;
using Sscp.ControlPlane.Domain.Common;

namespace Sscp.ControlPlane.Domain.Builds;

public enum BuildKind
{
    MainBranch,
    PullRequest,
    // A pull request on an application's GitOps repository: desired cluster state, checked
    // before it can merge.
    DeploymentChange,
}

public enum BuildStatus
{
    Requested,
    Running,
    Succeeded,
    Failed,
}

public sealed partial class Build
{
    private List<long> _runIds = [];

    private Build() { }

    public Guid Id { get; private set; }
    public string Application { get; private set; } = string.Empty;
    public string SourceRepository { get; private set; } = string.Empty;
    public string Commit { get; private set; } = string.Empty;
    public string Ref { get; private set; } = string.Empty;
    public BuildKind Kind { get; private set; }
    public int? PullRequest { get; private set; }
    public BuildStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public IReadOnlyList<long> PipelineRunIds => _runIds;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }

    public static Outcome<Build> Request(string application, string sourceRepository, string commit, string gitRef, BuildKind kind, int? pullRequest, DateTimeOffset now)
    {
        if (!CommitPattern().IsMatch(commit))
        {
            return DomainError.Validation("build.commit.invalid", "A commit must be a full 40-character SHA-1.");
        }

        if (kind == BuildKind.MainBranch && gitRef != "refs/heads/main")
        {
            return DomainError.Validation("build.ref.not-main", "Main builds are only created for refs/heads/main.");
        }

        return new Build
        {
            Id = Guid.CreateVersion7(),
            Application = application,
            SourceRepository = sourceRepository,
            Commit = commit,
            Ref = gitRef,
            Kind = kind,
            PullRequest = pullRequest,
            Status = BuildStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public Outcome AttachRun(long runId, DateTimeOffset now)
    {
        if (Status is BuildStatus.Succeeded or BuildStatus.Failed)
        {
            return DomainError.Conflict("build.finished", "A finished build cannot accept new pipeline runs.");
        }

        if (!_runIds.Contains(runId))
        {
            _runIds.Add(runId);
        }

        Status = BuildStatus.Running;
        return Touch(now);
    }

    public bool AcceptsRun(long runId) => _runIds.Contains(runId) && Status == BuildStatus.Running;

    public Outcome Complete(bool succeeded, string? reason, DateTimeOffset now)
    {
        if (Status is BuildStatus.Succeeded or BuildStatus.Failed)
        {
            return DomainError.Conflict("build.finished", "The build is already finished.");
        }

        Status = succeeded ? BuildStatus.Succeeded : BuildStatus.Failed;
        FailureReason = reason;
        return Touch(now);
    }

    private Outcome Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
        return Outcome.Ok();
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitPattern();
}
