// Builders for trust-evaluation inputs, so each test states only what differs from a
// fully clean, fully evidenced artifact.
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Policy;

namespace Sscp.ControlPlane.UnitTests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid BuildId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public const string Commit = "0123456789abcdef0123456789abcdef01234567";
    public const string ImageDigest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static readonly ArtifactUnderEvaluation EvaluatedArtifact = new(Guid.NewGuid(), BuildId, "commerce", "commerce-api", Commit, ImageDigest);

    public static readonly ExceptionPolicy Exceptions = new(90,
        new HashSet<EvidenceKind> { EvidenceKind.VulnerabilityScan, EvidenceKind.StaticAnalysis, EvidenceKind.InfrastructureScan, EvidenceKind.DockerfileLint, EvidenceKind.DynamicScan },
        20);

    public static readonly TrustPolicy Policy = new(
        "test-policy",
        [EvidenceKind.SecretScan, EvidenceKind.StaticAnalysis, EvidenceKind.CodeQuality, EvidenceKind.InfrastructureScan, EvidenceKind.DockerfileLint, EvidenceKind.DynamicScan, EvidenceKind.SecurityTests],
        [EvidenceKind.Sbom, EvidenceKind.VulnerabilityScan, EvidenceKind.SecondaryVulnerabilityScan],
        new SeverityGate(Severity.High, ExceptionsAllowed: true),
        new SeverityGate(Severity.High, ExceptionsAllowed: true),
        new SeverityGate(Severity.High, ExceptionsAllowed: true),
        new SeverityGate(Severity.High, ExceptionsAllowed: true),
        [
            new SlaRule(Severity.Critical, true, 0, SlaAction.Block),
            new SlaRule(Severity.Critical, false, 14, SlaAction.Block),
            new SlaRule(Severity.High, true, 7, SlaAction.Block),
            new SlaRule(Severity.High, false, 30, SlaAction.Block),
            new SlaRule(Severity.Medium, null, 30, SlaAction.Warn),
            new SlaRule(Severity.Low, null, 180, SlaAction.Track),
        ],
        TimeSpan.FromDays(7),
        Exceptions);

    public static EvidenceRecord Evidence(
        EvidenceKind kind,
        IEnumerable<Finding>? findings = null,
        ExecutionStatus execution = ExecutionStatus.Completed,
        string commit = Commit,
        string? digest = null,
        Guid? buildId = null,
        bool? gatePassed = null,
        DateTimeOffset? databaseUpdated = null,
        DateTimeOffset? receivedAt = null)
    {
        var subjectIsArtifact = EvidenceKinds.SubjectOf(kind) == EvidenceSubject.Artifact;
        return EvidenceRecord.Create(
            buildId ?? BuildId, "commerce", commit, kind,
            subjectIsArtifact ? "commerce-api" : null,
            subjectIsArtifact ? digest ?? ImageDigest : null,
            execution, execution == ExecutionStatus.Failed ? "scanner crashed" : null,
            new ToolInfo(kind.ToString().ToLowerInvariant(), "1.0", "db-1", databaseUpdated ?? Now.AddHours(-3)),
            new RawReport($"reports/{kind}", new string('0', 64), 10, "application/json"),
            "ci-security-zone", 42, "job", gatePassed ?? (kind is EvidenceKind.CodeQuality or EvidenceKind.SecurityTests or EvidenceKind.Sbom ? true : null), null,
            findings ?? [], receivedAt ?? Now.AddMinutes(-5));
    }

    public static List<EvidenceRecord> CleanEvidence() =>
        Policy.MandatoryCommitEvidence.Concat(Policy.MandatoryArtifactEvidence).Select(kind => Evidence(kind)).ToList();

    public static List<EvidenceRecord> Replace(List<EvidenceRecord> evidence, EvidenceRecord replacement)
    {
        evidence.RemoveAll(e => e.Kind == replacement.Kind);
        evidence.Add(replacement);
        return evidence;
    }

    public static Finding Vulnerability(string id, Severity severity, bool fixAvailable) => new()
    {
        Fingerprint = $"{id}|pkg:nuget/example@1.0.0",
        RuleId = id,
        Severity = severity,
        Title = id,
        Package = "example",
        InstalledVersion = "1.0.0",
        FixedVersion = fixAvailable ? "1.0.1" : null,
        FixAvailable = fixAvailable,
    };

    public static Finding Issue(string rule, Severity severity) => new()
    {
        Fingerprint = $"rule|{rule}|src/File.cs",
        RuleId = rule,
        Severity = severity,
        Title = rule,
        Location = "src/File.cs:10",
    };

    public static RiskException ApprovedException(Finding finding, EvidenceKind kind, string? deployable = null, DateTimeOffset? expires = null)
    {
        var requested = RiskException.Request("commerce", deployable, finding.Fingerprint, kind,
            "Upstream fix pending; exposure limited to internal network.", "no public route", "owner-1", expires ?? Now.AddDays(10), Exceptions, Now.AddDays(-1));
        requested.Value.Approve("approver-1", "accepted", Now.AddDays(-1));
        return requested.Value;
    }

    public static Decision Evaluate(
        List<EvidenceRecord> evidence,
        IReadOnlyList<RiskException>? exceptions = null,
        IReadOnlyDictionary<string, DateTimeOffset>? firstSeen = null,
        IReadOnlySet<Guid>? tampered = null,
        DateTimeOffset? now = null) =>
        TrustEvaluator.Evaluate(Policy, new EvaluationInput(EvaluatedArtifact, evidence, firstSeen ?? new Dictionary<string, DateTimeOffset>(),
            exceptions ?? [], tampered ?? new HashSet<Guid>(), now ?? Now));
}
