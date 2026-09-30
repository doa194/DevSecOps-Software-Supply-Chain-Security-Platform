// Loads the trust policy and the application catalogue from the platform repository's
// policy/ directory. Policy is code: it changes only through reviewed commits to the
// platform repository, and its content hash is recorded with every decision.
using System.Security.Cryptography;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Policy;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Sscp.ControlPlane.Infrastructure.Policy;

public sealed class PolicyOptions
{
    public const string SectionName = "Policy";
    public string TrustPolicyPath { get; set; } = "policy/trust-policy.yaml";
    public string ApplicationsPath { get; set; } = "policy/applications.yaml";
}

public sealed class YamlPolicyProvider : IPolicyProvider
{
    public YamlPolicyProvider(PolicyOptions options)
    {
        var content = File.ReadAllBytes(options.TrustPolicyPath);
        var document = Deserializer().Deserialize<TrustPolicyDocument>(System.Text.Encoding.UTF8.GetString(content))
            ?? throw new InvalidOperationException("The trust policy file is empty.");
        var version = $"{document.Name}@sha256:{Convert.ToHexStringLower(SHA256.HashData(content))[..16]}";
        Current = document.ToPolicy(version);
        InfrastructureSeverities = document.Infrastructure.SeverityOverrides.ToDictionary(
            pair => pair.Key, pair => Enum.Parse<Severity>(pair.Value, ignoreCase: true), StringComparer.Ordinal);
    }

    public TrustPolicy Current { get; }
    public IReadOnlyDictionary<string, Severity> InfrastructureSeverities { get; }

    internal static IDeserializer Deserializer() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();
}

public sealed class YamlApplicationCatalog : IApplicationCatalog
{
    private readonly IReadOnlyList<RegisteredApplication> _applications;

    public YamlApplicationCatalog(PolicyOptions options)
    {
        var document = YamlPolicyProvider.Deserializer().Deserialize<ApplicationsDocument>(File.ReadAllText(options.ApplicationsPath));
        _applications = document.Applications.Select(a => new RegisteredApplication(a.Name, a.SourceRepository, a.Deployables, a.CandidateRepository, a.TrustedRepository,
            a.GitOps is null ? null : new GitOpsTarget(a.GitOps.Repository, a.GitOps.Path, a.GitOps.Environment))).ToList();
    }

    public RegisteredApplication? Find(string name) => _applications.FirstOrDefault(a => a.Name == name);

    public RegisteredApplication? FindBySourceRepository(string repository) =>
        _applications.FirstOrDefault(a => string.Equals(a.SourceRepository, repository, StringComparison.OrdinalIgnoreCase));

    public RegisteredApplication? FindByGitOpsRepository(string repository) =>
        _applications.FirstOrDefault(a => a.GitOps is not null && string.Equals(a.GitOps.Repository, repository, StringComparison.OrdinalIgnoreCase));
}

// YAML shapes. Kept separate from the domain so the file format can evolve independently.
public sealed class TrustPolicyDocument
{
    public string Name { get; set; } = "trust-policy";
    public MandatoryEvidenceDocument MandatoryEvidence { get; set; } = new();
    public Dictionary<string, GateDocument> Gates { get; set; } = [];
    public InfrastructureDocument Infrastructure { get; set; } = new();
    public VulnerabilityDocument Vulnerabilities { get; set; } = new();
    public ExceptionsDocument Exceptions { get; set; } = new();

    public TrustPolicy ToPolicy(string version)
    {
        SeverityGate Gate(string name) => Gates.TryGetValue(name, out var gate)
            ? new SeverityGate(Enum.Parse<Severity>(gate.BlockAtOrAbove, true), gate.ExceptionsAllowed)
            : throw new InvalidOperationException($"The trust policy has no '{name}' gate.");

        var policy = new TrustPolicy(
            version,
            MandatoryEvidence.Commit.Select(k => Enum.Parse<EvidenceKind>(k, true)).ToList(),
            MandatoryEvidence.Artifact.Select(k => Enum.Parse<EvidenceKind>(k, true)).ToList(),
            Gate("staticAnalysis"), Gate("infrastructure"), Gate("dockerfile"), Gate("dynamicScan"),
            Vulnerabilities.Sla.Select(rule => new SlaRule(Enum.Parse<Severity>(rule.Severity, true), rule.FixAvailable, rule.RemediationDays, Enum.Parse<SlaAction>(rule.AfterWindow, true))).ToList(),
            TimeSpan.FromDays(Vulnerabilities.MaxDatabaseAgeDays),
            new ExceptionPolicy(Exceptions.MaxDays, Exceptions.AllowedKinds.Select(k => Enum.Parse<EvidenceKind>(k, true)).ToHashSet(), Exceptions.MinimumJustificationLength));
        return policy with
        {
            MandatoryPullRequestEvidence = MandatoryEvidence.PullRequest.Select(k => Enum.Parse<EvidenceKind>(k, true)).ToList(),
            MandatoryDeploymentChangeEvidence = MandatoryEvidence.DeploymentChange.Select(k => Enum.Parse<EvidenceKind>(k, true)).ToList(),
        };
    }
}

public sealed class MandatoryEvidenceDocument
{
    public List<string> Commit { get; set; } = [];
    public List<string> Artifact { get; set; } = [];
    public List<string> PullRequest { get; set; } = [];
    public List<string> DeploymentChange { get; set; } = [];
}

public sealed class GateDocument
{
    public string BlockAtOrAbove { get; set; } = "High";
    public bool ExceptionsAllowed { get; set; }
}

public sealed class InfrastructureDocument
{
    public Dictionary<string, string> SeverityOverrides { get; set; } = [];
}

public sealed class VulnerabilityDocument
{
    public int MaxDatabaseAgeDays { get; set; } = 7;
    public List<SlaRuleDocument> Sla { get; set; } = [];
}

public sealed class SlaRuleDocument
{
    public string Severity { get; set; } = "Low";
    public bool? FixAvailable { get; set; }
    public int RemediationDays { get; set; }
    public string AfterWindow { get; set; } = "Track";
}

public sealed class ExceptionsDocument
{
    public int MaxDays { get; set; } = 90;
    public int MinimumJustificationLength { get; set; } = 20;
    public List<string> AllowedKinds { get; set; } = [];
}

public sealed class ApplicationsDocument
{
    public List<ApplicationDocument> Applications { get; set; } = [];
}

public sealed class ApplicationDocument
{
    public string Name { get; set; } = string.Empty;
    public string SourceRepository { get; set; } = string.Empty;
    public List<string> Deployables { get; set; } = [];
    public string CandidateRepository { get; set; } = string.Empty;
    public string TrustedRepository { get; set; } = string.Empty;
    public GitOpsDocument? GitOps { get; set; }
}

public sealed class GitOpsDocument
{
    public string Repository { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Environment { get; set; } = "local";
}
