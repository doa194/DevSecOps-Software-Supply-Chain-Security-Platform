// A release: a protected tag on a main commit whose artifacts go through the trust zone.
//
//   Requested ─▶ Approved / ApprovedWithException ─▶ Signed ─▶ Promoted ─▶ GitOpsUpdated ─▶ Deployed
//       │                 │
//       ▼                 ▼
//    Rejected ◀── (re-evaluation right before signing, e.g. an exception expired)
//   Any unfinished state ─▶ Failed (pipeline error)
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Policy;

namespace Sscp.ControlPlane.Domain.Releases;

public enum ReleaseState
{
    Requested,
    Rejected,
    Approved,
    ApprovedWithException,
    Signed,
    Promoted,
    GitOpsUpdated,
    Deployed,
    Failed,
}

public sealed record ReleaseStateChanged(Guid ReleaseId, ReleaseState From, ReleaseState To, string Actor, string Reason, DateTimeOffset At);

public sealed class Release
{
    private static readonly Dictionary<ReleaseState, ReleaseState[]> Allowed = new()
    {
        [ReleaseState.Requested] = [ReleaseState.Rejected, ReleaseState.Approved, ReleaseState.ApprovedWithException, ReleaseState.Failed],
        [ReleaseState.Approved] = [ReleaseState.Rejected, ReleaseState.ApprovedWithException, ReleaseState.Signed, ReleaseState.Failed],
        [ReleaseState.ApprovedWithException] = [ReleaseState.Rejected, ReleaseState.Approved, ReleaseState.Signed, ReleaseState.Failed],
        [ReleaseState.Signed] = [ReleaseState.Promoted, ReleaseState.Failed],
        [ReleaseState.Promoted] = [ReleaseState.GitOpsUpdated, ReleaseState.Failed],
        [ReleaseState.GitOpsUpdated] = [ReleaseState.Deployed, ReleaseState.Failed],
        [ReleaseState.Deployed] = [],
        [ReleaseState.Rejected] = [ReleaseState.Approved, ReleaseState.ApprovedWithException],
        [ReleaseState.Failed] = [],
    };

    private List<Guid> _artifactIds = [];
    private readonly List<ReleaseStateChanged> _changes = [];

    private Release() { }

    public Guid Id { get; private set; }
    public string Application { get; private set; } = string.Empty;
    public string Tag { get; private set; } = string.Empty;
    public string Commit { get; private set; } = string.Empty;
    public Guid BuildId { get; private set; }
    public string RequestedBy { get; private set; } = string.Empty;
    public ReleaseState State { get; private set; }
    public long? PipelineRunId { get; private set; }
    public string? GitOpsCommit { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<Guid> ArtifactIds => _artifactIds;
    public IReadOnlyList<ReleaseStateChanged> PendingChanges => _changes;
    public int Version { get; private set; }

    public static Outcome<Release> Request(string application, string tag, string commit, Guid buildId, IEnumerable<Guid> artifactIds, string requestedBy, DateTimeOffset now)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$"))
        {
            return DomainError.Validation("release.tag.invalid", "Release tags must look like v1.2.3.");
        }

        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            Application = application,
            Tag = tag,
            Commit = commit,
            BuildId = buildId,
            RequestedBy = requestedBy,
            State = ReleaseState.Requested,
            RequestedAt = now,
            UpdatedAt = now,
        };
        release._artifactIds.AddRange(artifactIds);
        return release.ArtifactIds.Count == 0
            ? DomainError.Conflict("release.artifacts.missing", "The commit has no candidate artifacts to release.")
            : release;
    }

    // A release is carried out by exactly one trust pipeline run (reruns keep the run id).
    public Outcome AttachRun(long runId, DateTimeOffset now)
    {
        if (PipelineRunId is { } existing && existing != runId)
        {
            return DomainError.Conflict("release.run.already-attached", $"The release is already bound to pipeline run {existing}.");
        }

        PipelineRunId = runId;
        UpdatedAt = now;
        return Outcome.Ok();
    }

    public bool AcceptsRun(long runId) => PipelineRunId == runId;

    public Outcome ApplyDecision(DecisionOutcome outcome, string actor, DateTimeOffset now)
    {
        var target = outcome switch
        {
            DecisionOutcome.Pass => ReleaseState.Approved,
            DecisionOutcome.PassWithException => ReleaseState.ApprovedWithException,
            _ => ReleaseState.Rejected,
        };
        return State == target ? Outcome.Ok() : Move(target, actor, $"decision {outcome}", now);
    }

    public bool MaySign => State is ReleaseState.Approved or ReleaseState.ApprovedWithException;

    public Outcome MarkSigned(string actor, DateTimeOffset now) => Move(ReleaseState.Signed, actor, "all artifacts signed", now);
    public Outcome MarkPromoted(string actor, DateTimeOffset now) => Move(ReleaseState.Promoted, actor, "all artifacts promoted", now);

    public Outcome MarkGitOpsUpdated(string gitOpsCommit, string actor, DateTimeOffset now)
    {
        var moved = Move(ReleaseState.GitOpsUpdated, actor, $"desired state committed as {gitOpsCommit}", now);
        if (moved.Succeeded)
        {
            GitOpsCommit = gitOpsCommit;
        }

        return moved;
    }

    public Outcome MarkDeployed(string actor, DateTimeOffset now) => Move(ReleaseState.Deployed, actor, "reconciled and healthy", now);

    public Outcome Fail(string reason, string actor, DateTimeOffset now)
    {
        var moved = Move(ReleaseState.Failed, actor, reason, now);
        if (moved.Succeeded)
        {
            FailureReason = reason;
        }

        return moved;
    }

    private Outcome Move(ReleaseState to, string actor, string reason, DateTimeOffset now)
    {
        if (!Allowed[State].Contains(to))
        {
            return DomainError.Conflict("release.transition.invalid", $"A release that is {State} cannot become {to}.");
        }

        _changes.Add(new ReleaseStateChanged(Id, State, to, actor, reason, now));
        State = to;
        UpdatedAt = now;
        Version++;
        return Outcome.Ok();
    }

    public void ClearPendingChanges() => _changes.Clear();
}
