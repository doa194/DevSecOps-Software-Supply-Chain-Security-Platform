// Scanner reports in each tool's own JSON format, reduced to the fields the Control Plane
// reads. Tests build clean reports or add specific findings to steer a decision.
using System.Text;
using System.Text.Json;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.ComponentTests.Infrastructure;

public sealed record Vulnerability(string Id, string Severity, string Package = "System.Text.Json", string Installed = "8.0.0", string? Fixed = "8.0.5");

public static class Reports
{
    public static byte[] For(EvidenceKind kind, string? repository = null, string? digest = null, IReadOnlyList<Vulnerability>? vulnerabilities = null) => kind switch
    {
        EvidenceKind.SecretScan => Json(Array.Empty<object>()),
        EvidenceKind.StaticAnalysis => Json(new
        {
            version = "2.1.0",
            runs = new[] { new { tool = new { driver = new { name = "Semgrep OSS", semanticVersion = "1.178.0" } }, results = Array.Empty<object>() } },
        }),
        EvidenceKind.CodeQuality => Json(new { projectStatus = new { status = "OK", conditions = Array.Empty<object>() } }),
        EvidenceKind.InfrastructureScan => Json(new
        {
            check_type = "dockerfile",
            results = new { passed_checks = Array.Empty<object>(), failed_checks = Array.Empty<object>() },
            summary = new { passed = 12, failed = 0, checkov_version = "3.3.19" },
        }),
        EvidenceKind.DockerfileLint => Json(Array.Empty<object>()),
        EvidenceKind.DynamicScan => Json(new Dictionary<string, object>
        {
            ["@version"] = "2.17.0",
            ["site"] = new[] { new Dictionary<string, object> { ["@name"] = "https://commerce.sscp.test", ["alerts"] = Array.Empty<object>() } },
        }),
        EvidenceKind.SecurityTests => Json(new
        {
            schemaVersion = 1,
            suite = "commerce-authorization",
            results = new[] { new { name = "customer cannot read another customer's order", passed = true } },
        }),
        EvidenceKind.Sbom => Json(new
        {
            bomFormat = "CycloneDX",
            specVersion = "1.6",
            metadata = new
            {
                component = new { type = "container", name = repository, version = digest },
                tools = new { components = new[] { new { name = "syft", version = "1.52.0" } } },
            },
            components = new[] { new { type = "library", name = "System.Text.Json", version = "10.0.0" } },
        }),
        EvidenceKind.VulnerabilityScan => Json(new
        {
            SchemaVersion = 2,
            ArtifactName = $"{repository}@{digest}",
            Metadata = new { RepoDigests = new[] { $"{repository}@{digest}" } },
            Results = new[]
            {
                new
                {
                    Target = "app/Commerce.Api.deps.json",
                    Vulnerabilities = (vulnerabilities ?? []).Select(v => new
                    {
                        VulnerabilityID = v.Id,
                        PkgName = v.Package,
                        InstalledVersion = v.Installed,
                        FixedVersion = v.Fixed,
                        Severity = v.Severity.ToUpperInvariant(),
                        Title = $"{v.Id} in {v.Package}",
                    }).ToArray(),
                },
            },
        }),
        EvidenceKind.SecondaryVulnerabilityScan => Json(new
        {
            descriptor = new { name = "grype", version = "0.119.0", db = new { status = new { schemaVersion = "v6.1.0", built = DateTimeOffset.UtcNow.ToString("O") } } },
            source = new { type = "image", target = new { userInput = $"{repository}@{digest}", manifestDigest = digest } },
            matches = Array.Empty<object>(),
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No test report for this kind."),
    };

    // A redacted Gitleaks finding, as `gitleaks --redact` reports it.
    public static byte[] Leak(string file = "src/Settings.cs") => Json(new[]
    {
        new { RuleID = "generic-api-key", Description = "Generic API Key", File = file, StartLine = 12, Secret = "REDACTED", Match = "apiKey = REDACTED" },
    });

    private static byte[] Json(object value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
}
