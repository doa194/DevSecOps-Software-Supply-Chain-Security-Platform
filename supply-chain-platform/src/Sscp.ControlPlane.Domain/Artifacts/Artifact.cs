// An immutable image produced by one build, and its explicit trust lifecycle.
//
//   Candidate ─▶ EvidencePending ─▶ Approved ───────────────┐
//                       │   ▲        ApprovedWithException ──┤─▶ Signed ─▶ Promoted ─▶ Deployed
//                       ▼   │                                 │
//                     Rejected ◀── (re-evaluation before signing)
//
// Only the transitions drawn here are possible. In particular an artifact can be signed
// only after a positive decision, promoted only after it was signed, and a rejected
// artifact can come back only through a new evaluation with new evidence or exceptions.
using Sscp.ControlPlane.Domain.Common;

namespace Sscp.ControlPlane.Domain.Artifacts;

public enum ArtifactState
{
    Candidate,
    EvidencePending,
    Rejected,
    Approved,
    ApprovedWithException,
    Signed,
    Promoted,
    Deployed,
}

public static class ArtifactStateMachine
{
    private static readonly Dictionary<ArtifactState, ArtifactState[]> Allowed = new()
    {
        [ArtifactState.Candidate] = [ArtifactState.EvidencePending, ArtifactState.Rejected, ArtifactState.Approved, ArtifactState.ApprovedWithException],
        [ArtifactState.EvidencePending] = [ArtifactState.Rejected, ArtifactState.Approved, ArtifactState.ApprovedWithException],
        [ArtifactState.Rejected] = [ArtifactState.EvidencePending, ArtifactState.Approved, ArtifactState.ApprovedWithException],
        [ArtifactState.Approved] = [ArtifactState.Rejected, ArtifactState.ApprovedWithException, ArtifactState.Signed],
        [ArtifactState.ApprovedWithException] = [ArtifactState.Rejected, ArtifactState.Approved, ArtifactState.Signed],
        [ArtifactState.Signed] = [ArtifactState.Promoted],
        [ArtifactState.Promoted] = [ArtifactState.Deployed],
        [ArtifactState.Deployed] = [ArtifactState.Deployed],
    };

    public static bool CanMove(ArtifactState from, ArtifactState to) => Allowed[from].Contains(to);

    public static bool IsTrusted(ArtifactState state) =>
        state is ArtifactState.Approved or ArtifactState.ApprovedWithException or ArtifactState.Signed or ArtifactState.Promoted or ArtifactState.Deployed;
}

public sealed record ArtifactStateChanged(Guid ArtifactId, ArtifactState From, ArtifactState To, string Actor, string Reason, DateTimeOffset At);

public sealed class Artifact
{
    private readonly List<ArtifactStateChanged> _changes = [];

    private Artifact() { }

    public Guid Id { get; private set; }
    public Guid BuildId { get; private set; }
    public string Application { get; private set; } = string.Empty;
    public string Deployable { get; private set; } = string.Empty;
    public string Commit { get; private set; } = string.Empty;
    public string CandidateRepository { get; private set; } = string.Empty;
    public string Digest { get; private set; } = string.Empty;
    public string? TrustedRepository { get; private set; }
    public ArtifactState State { get; private set; }
    public Guid? LatestDecisionId { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; }

    // Changes made since load; the application layer turns them into audit records.
    public IReadOnlyList<ArtifactStateChanged> PendingChanges => _changes;

    public string CandidateReference => $"{CandidateRepository}@{Digest}";

    public static Artifact Register(Guid buildId, string application, string deployable, string commit, ArtifactReference candidate, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        BuildId = buildId,
        Application = application,
        Deployable = deployable,
        Commit = commit,
        CandidateRepository = candidate.Repository,
        Digest = candidate.Digest.Value,
        State = ArtifactState.Candidate,
        RegisteredAt = now,
        UpdatedAt = now,
    };

    public Outcome MarkEvidencePending(string actor, DateTimeOffset now) =>
        State is ArtifactState.EvidencePending ? Outcome.Ok() : Move(ArtifactState.EvidencePending, actor, "evidence received", now);

    public Outcome ApplyDecision(Guid decisionId, Policy.DecisionOutcome outcome, string actor, DateTimeOffset now)
    {
        if (State is ArtifactState.Signed or ArtifactState.Promoted or ArtifactState.Deployed)
        {
            // Already cryptographically trusted: a new decision is recorded but cannot rewind
            // the lifecycle. It blocks further releases of this digest instead.
            LatestDecisionId = decisionId;
            UpdatedAt = now;
            return Outcome.Ok();
        }

        var target = outcome switch
        {
            Policy.DecisionOutcome.Pass => ArtifactState.Approved,
            Policy.DecisionOutcome.PassWithException => ArtifactState.ApprovedWithException,
            _ => ArtifactState.Rejected,
        };
        LatestDecisionId = decisionId;
        return State == target ? Touch(now) : Move(target, actor, $"decision {outcome}", now);
    }

    // A later release of the same build (a new tag on the same commit) signs and promotes
    // the digest again. It adds its own signature and promotion records; the artifact's
    // lifecycle stays where it is instead of moving backwards.
    public Outcome MarkSigned(string actor, DateTimeOffset now) =>
        State is ArtifactState.Signed or ArtifactState.Promoted or ArtifactState.Deployed
            ? Touch(now)
            : Move(ArtifactState.Signed, actor, "signature recorded", now);

    public Outcome MarkPromoted(string trustedRepository, string actor, DateTimeOffset now)
    {
        var moved = State is ArtifactState.Promoted or ArtifactState.Deployed
            ? Touch(now)
            : Move(ArtifactState.Promoted, actor, $"promoted to {trustedRepository}", now);
        if (moved.Succeeded)
        {
            TrustedRepository = trustedRepository;
        }

        return moved;
    }

    public Outcome MarkDeployed(string actor, DateTimeOffset now) =>
        State == ArtifactState.Deployed ? Touch(now) : Move(ArtifactState.Deployed, actor, "running in the cluster", now);

    private Outcome Move(ArtifactState to, string actor, string reason, DateTimeOffset now)
    {
        if (!ArtifactStateMachine.CanMove(State, to))
        {
            return DomainError.Conflict("artifact.transition.invalid", $"An artifact that is {State} cannot become {to}.");
        }

        _changes.Add(new ArtifactStateChanged(Id, State, to, actor, reason, now));
        State = to;
        return Touch(now);
    }

    private Outcome Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
        return Outcome.Ok();
    }

    public void ClearPendingChanges() => _changes.Clear();
}
