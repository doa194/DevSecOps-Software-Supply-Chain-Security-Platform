// The trust policy as data. It is loaded from policy/trust-policy.yaml and identified by
// the hash of that file, so every recorded decision names the exact policy it applied.
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Domain.Policy;

public enum DecisionOutcome
{
    Pass,
    Fail,
    PassWithException,
}

public static class DecisionOutcomes
{
    // The names used in commit statuses, reports and signed attestations.
    public static string Name(DecisionOutcome outcome) => outcome switch
    {
        DecisionOutcome.Pass => "PASS",
        DecisionOutcome.PassWithException => "PASS_WITH_EXCEPTION",
        _ => "FAIL",
    };
}

// What happens to a finding once its remediation window has passed.
public enum SlaAction
{
    Block,
    Warn,
    Track,
}

public sealed record SeverityGate(Severity BlockAtOrAbove, bool ExceptionsAllowed);

// One row of the vulnerability SLA table. FixAvailable = null matches both cases.
public sealed record SlaRule(Severity Severity, bool? FixAvailable, int RemediationDays, SlaAction AfterWindow);

public sealed record ExceptionPolicy(int MaxDays, IReadOnlySet<EvidenceKind> AllowedKinds, int MinimumJustificationLength);

public sealed record TrustPolicy(
    string Version,
    IReadOnlyList<EvidenceKind> MandatoryCommitEvidence,
    IReadOnlyList<EvidenceKind> MandatoryArtifactEvidence,
    SeverityGate StaticAnalysis,
    SeverityGate Infrastructure,
    SeverityGate DockerfileLint,
    SeverityGate DynamicScan,
    IReadOnlyList<SlaRule> VulnerabilitySla,
    TimeSpan MaxVulnerabilityDatabaseAge,
    ExceptionPolicy Exceptions)
{
    // Commit evidence required by the pull-request (source-only) gate, which runs before
    // any image exists.
    public IReadOnlyList<EvidenceKind> MandatoryPullRequestEvidence { get; init; } = [];

    // Evidence required by the deployment-change gate on GitOps pull requests.
    public IReadOnlyList<EvidenceKind> MandatoryDeploymentChangeEvidence { get; init; } = [];

    public SlaRule SlaFor(Severity severity, bool fixAvailable) =>
        VulnerabilitySla.FirstOrDefault(rule => rule.Severity == severity && (rule.FixAvailable is null || rule.FixAvailable == fixAvailable))
        // Anything the table does not mention is tracked, never silently ignored.
        ?? new SlaRule(severity, null, int.MaxValue, SlaAction.Track);
}
