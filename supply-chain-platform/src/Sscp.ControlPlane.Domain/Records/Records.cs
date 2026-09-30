// Recorded facts about artifacts that do not have their own lifecycle: trust decisions,
// signatures, promotions and deployments. They are append-only history.
using Sscp.ControlPlane.Domain.Policy;

namespace Sscp.ControlPlane.Domain.Records;

public sealed class TrustDecisionRecord
{
    public Guid Id { get; init; }
    public Guid ArtifactId { get; init; }
    public Guid? ReleaseId { get; init; }
    public DecisionOutcome Outcome { get; init; }
    public required string PolicyVersion { get; init; }
    public required string EvaluatedBy { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
    public required IReadOnlyList<RuleResult> Results { get; init; }
    public required IReadOnlyList<Guid> AppliedExceptions { get; init; }
    public required IReadOnlyList<Guid> EvidenceUsed { get; init; }

    public static TrustDecisionRecord From(Guid artifactId, Guid? releaseId, Decision decision, string actor, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ArtifactId = artifactId,
        ReleaseId = releaseId,
        Outcome = decision.Outcome,
        PolicyVersion = decision.PolicyVersion,
        EvaluatedBy = actor,
        EvaluatedAt = now,
        Results = decision.Results,
        AppliedExceptions = decision.AppliedExceptions,
        EvidenceUsed = decision.EvidenceUsed,
    };
}

public sealed class SignatureRecord
{
    public Guid Id { get; init; }
    public Guid ArtifactId { get; init; }
    public Guid ReleaseId { get; init; }
    public required string Digest { get; init; }
    public required string KeyReference { get; init; }
    public required string SignatureReference { get; init; }
    public IReadOnlyList<string> AttestationReferences { get; init; } = [];
    public long PipelineRunId { get; init; }
    public DateTimeOffset SignedAt { get; init; }
}

public sealed class PromotionRecord
{
    public Guid Id { get; init; }
    public Guid ArtifactId { get; init; }
    public Guid ReleaseId { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public long PipelineRunId { get; init; }
    public DateTimeOffset PromotedAt { get; init; }
}

public sealed class DeploymentRecord
{
    public Guid Id { get; init; }
    public required string Application { get; init; }
    public Guid? ReleaseId { get; init; }
    public required string Environment { get; init; }
    public required string GitOpsRevision { get; init; }
    public required string SyncStatus { get; init; }
    public required string HealthStatus { get; init; }
    public IReadOnlyList<string> Images { get; init; } = [];
    public DateTimeOffset ObservedAt { get; init; }
}
