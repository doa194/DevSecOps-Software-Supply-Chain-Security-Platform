// Security evidence: what a scanner found about a commit or an image digest.
//
// The Control Plane stores the raw report in the evidence bucket and keeps here the
// metadata that correlates it: which build, commit and digest it describes, which tool
// and database produced it, whether the tool actually ran, and the normalised findings.
namespace Sscp.ControlPlane.Domain.Evidence;

public enum EvidenceKind
{
    SecretScan,
    StaticAnalysis,
    CodeQuality,
    InfrastructureScan,
    DockerfileLint,
    Sbom,
    VulnerabilityScan,
    SecondaryVulnerabilityScan,
    DynamicScan,
    SecurityTests,
    DeploymentConfigScan,
    Provenance,
    ClusterScan,
}

// What an evidence record is about: the source at a commit, or one image digest.
public enum EvidenceSubject
{
    Commit,
    Artifact,
}

public static class EvidenceKinds
{
    public static EvidenceSubject SubjectOf(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Sbom or EvidenceKind.VulnerabilityScan or EvidenceKind.SecondaryVulnerabilityScan or EvidenceKind.Provenance => EvidenceSubject.Artifact,
        _ => EvidenceSubject.Commit,
    };
}

// Whether the scanner ran to completion. A scanner that crashed, timed out or could not
// load its database produced no evidence of safety, whatever its report says.
public enum ExecutionStatus
{
    Completed,
    Failed,
}

public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

public sealed record RawReport(string ObjectKey, string Sha256, long SizeBytes, string ContentType);

public sealed record ToolInfo(string Name, string Version, string? DatabaseVersion, DateTimeOffset? DatabaseUpdatedAt);

public sealed class Finding
{
    public Guid Id { get; init; }
    public Guid EvidenceId { get; init; }

    // Stable identity used for exceptions and SLA age, e.g. "CVE-2024-1234|pkg:nuget/log4net@2.0.9"
    // or "semgrep|csharp.sensitive-logging|src/Orders/PlaceOrder.cs".
    public required string Fingerprint { get; init; }
    public required string RuleId { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public string? Location { get; init; }
    public string? Package { get; init; }
    public string? InstalledVersion { get; init; }
    public string? FixedVersion { get; init; }
    public bool FixAvailable { get; init; }
}

public sealed class EvidenceRecord
{
    private readonly List<Finding> _findings = [];

    private EvidenceRecord() { }

    public Guid Id { get; private set; }
    public Guid BuildId { get; private set; }
    public string Application { get; private set; } = string.Empty;
    public string Commit { get; private set; } = string.Empty;
    public EvidenceKind Kind { get; private set; }
    public string? Deployable { get; private set; }
    public string? ArtifactDigest { get; private set; }
    public ExecutionStatus Execution { get; private set; }
    public string? ExecutionError { get; private set; }
    public ToolInfo Tool { get; private set; } = new("unknown", "unknown", null, null);
    public RawReport Report { get; private set; } = new(string.Empty, string.Empty, 0, string.Empty);
    public string SubmittedBy { get; private set; } = string.Empty;
    public long PipelineRunId { get; private set; }
    public string JobName { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }

    // Kind-specific pass/fail signal that is not a list of findings, e.g. the SonarQube
    // quality gate status or failed security tests.
    public bool? GatePassed { get; private set; }
    public string? GateDetail { get; private set; }

    public IReadOnlyList<Finding> Findings => _findings;

    public static EvidenceRecord Create(
        Guid buildId, string application, string commit, EvidenceKind kind, string? deployable, string? artifactDigest,
        ExecutionStatus execution, string? executionError, ToolInfo tool, RawReport report, string submittedBy, long pipelineRunId,
        string jobName, bool? gatePassed, string? gateDetail, IEnumerable<Finding> findings, DateTimeOffset now)
    {
        var record = new EvidenceRecord
        {
            Id = Guid.CreateVersion7(),
            BuildId = buildId,
            Application = application,
            Commit = commit,
            Kind = kind,
            Deployable = deployable,
            ArtifactDigest = artifactDigest,
            Execution = execution,
            ExecutionError = executionError,
            Tool = tool,
            Report = report,
            SubmittedBy = submittedBy,
            PipelineRunId = pipelineRunId,
            JobName = jobName,
            GatePassed = gatePassed,
            GateDetail = gateDetail,
            ReceivedAt = now,
        };
        record._findings.AddRange(findings.Select(f => new Finding
        {
            Id = Guid.CreateVersion7(),
            EvidenceId = record.Id,
            Fingerprint = f.Fingerprint,
            RuleId = f.RuleId,
            Severity = f.Severity,
            Title = f.Title,
            Location = f.Location,
            Package = f.Package,
            InstalledVersion = f.InstalledVersion,
            FixedVersion = f.FixedVersion,
            FixAvailable = f.FixAvailable,
        }));
        return record;
    }

    public IReadOnlyDictionary<Severity, int> SeverityCounts() =>
        Enum.GetValues<Severity>().ToDictionary(severity => severity, severity => _findings.Count(f => f.Severity == severity));
}
