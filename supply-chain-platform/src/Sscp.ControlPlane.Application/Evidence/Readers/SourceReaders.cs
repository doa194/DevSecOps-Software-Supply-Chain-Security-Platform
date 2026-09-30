// Readers for source-level evidence: Gitleaks, Semgrep (SARIF), Checkov, Hadolint and the
// SonarQube quality gate. Source evidence is bound to a commit; the pipeline scans a clean
// checkout of exactly that commit.
using System.Text.Json;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Application.Evidence.Readers;

// Gitleaks JSON report (array of leaks). Reports must be redacted: the evidence store must
// never become a second copy of the leaked secret.
public sealed class GitleaksReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.SecretScan;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "Gitleaks");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ReportRejectedException("A Gitleaks report is a JSON array.");
        }

        var findings = new List<Finding>();
        foreach (var leak in document.RootElement.EnumerateArray())
        {
            var secret = leak.String("Secret");
            if (!string.IsNullOrEmpty(secret) && secret != "REDACTED")
            {
                throw new ReportRejectedException("Gitleaks reports must be produced with --redact; unredacted secrets are not stored.");
            }

            var rule = leak.String("RuleID") ?? "unknown-rule";
            var file = JsonHelpers.RelativePath(leak.String("File"));
            findings.Add(new Finding
            {
                Fingerprint = $"gitleaks|{rule}|{file}|{leak.Int("StartLine") ?? 0}",
                RuleId = rule,
                Severity = Severity.Critical,
                Title = leak.String("Description") ?? rule,
                Location = $"{file}:{leak.Int("StartLine") ?? 0}",
            });
        }

        return new ParsedReport(findings, "gitleaks", context.Meta("toolVersion") ?? "unknown", null, null, null, null);
    }
}

// SARIF 2.1 as produced by Semgrep. Semgrep OSS does not compute match-based fingerprints
// ("requires login"), so a finding is keyed by rule, file and a hash of the matched code:
// an exception keeps applying when unrelated lines move, and stops when the code changes.
// The severity comes from the result, or else from the rule's default level.
public sealed class SarifReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.StaticAnalysis;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "SARIF");
        var runs = document.RootElement.Array("runs").ToList();
        if (runs.Count == 0)
        {
            throw new ReportRejectedException("A SARIF report must contain at least one run.");
        }

        var driver = runs[0].Object("tool")?.Object("driver");
        var defaultLevels = runs.SelectMany(run => run.Object("tool")?.Object("driver")?.Array("rules") ?? [])
            .Where(rule => rule.String("id") is not null)
            .GroupBy(rule => rule.String("id")!)
            .ToDictionary(group => group.Key, group => group.First().Object("defaultConfiguration")?.String("level"), StringComparer.Ordinal);

        var findings = new List<Finding>();
        foreach (var result in runs.SelectMany(run => run.Array("results")))
        {
            var rawRule = result.String("ruleId") ?? "unknown-rule";
            var rule = NormaliseRuleId(rawRule);
            var location = result.Array("locations").FirstOrDefault().Object("physicalLocation");
            var path = JsonHelpers.RelativePath(location?.Object("artifactLocation")?.String("uri"));
            var region = location?.Object("region");
            var line = region?.Int("startLine") ?? 0;
            var snippet = region?.Object("snippet")?.String("text");
            var matchKey = snippet is null
                ? line.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(string.Join(' ', snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))))[..16];
            var level = result.String("level") ?? defaultLevels.GetValueOrDefault(rawRule);
            findings.Add(new Finding
            {
                Fingerprint = $"semgrep|{rule}|{path}|{matchKey}",
                RuleId = rule,
                Severity = level switch
                {
                    "error" => Severity.High,
                    "warning" => Severity.Medium,
                    "note" => Severity.Low,
                    _ => Severity.Info,
                },
                Title = result.Object("message")?.String("text") ?? rule,
                Location = $"{path}:{line}",
            });
        }

        return new ParsedReport(findings, driver?.String("name") ?? "semgrep", driver?.String("semanticVersion") ?? driver?.String("version") ?? context.Meta("toolVersion") ?? "unknown", null, null, null, null);
    }

    // Semgrep prefixes rule ids with the directory the rules were loaded from
    // ("rules.semgrep.sscp.dotnet.x"); findings are recorded under the rule's own id.
    private static string NormaliseRuleId(string ruleId)
    {
        var start = ruleId.IndexOf("sscp.", StringComparison.Ordinal);
        return start > 0 ? ruleId[start..] : ruleId;
    }
}

// Checkov JSON (one object, or an array with one object per framework). Checkov's free edition
// does not grade checks, so every failed check counts as High unless the trust policy lists
// a different severity for that check id.
//
// Skipped checks count as failed: the platform never skips checks when it runs Checkov, so a
// skip can only come from a `checkov:skip` comment written by the code's own author.
public sealed class CheckovReader(IReadOnlyDictionary<string, Severity> severityOverrides, EvidenceKind kind = EvidenceKind.InfrastructureScan) : IEvidenceReader
{
    public EvidenceKind Kind => kind;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "Checkov");
        var root = document.RootElement;
        var sections = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        if (sections.Count == 0 || sections.All(s => s.Object("summary") is null))
        {
            throw new ReportRejectedException("A Checkov report must contain a summary.");
        }

        var findings = new List<Finding>();
        string? version = null;
        foreach (var section in sections)
        {
            version ??= section.Object("summary")?.String("checkov_version");
            var results = section.Object("results");
            foreach (var check in (results?.Array("failed_checks") ?? []).Concat(results?.Array("skipped_checks") ?? []))
            {
                var id = check.String("check_id") ?? "unknown-check";
                var file = JsonHelpers.RelativePath(check.String("file_path"));
                var resource = check.String("resource") ?? "unknown-resource";
                findings.Add(new Finding
                {
                    Fingerprint = $"checkov|{id}|{file}|{resource}",
                    RuleId = id,
                    Severity = severityOverrides.TryGetValue(id, out var severity) ? severity : Severity.High,
                    Title = check.String("check_name") ?? id,
                    Location = $"{file} ({resource})",
                });
            }
        }

        return new ParsedReport(findings, "checkov", version ?? context.Meta("toolVersion") ?? "unknown", null, null, null, null);
    }
}

// Hadolint JSON (array of rule violations).
public sealed class HadolintReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.DockerfileLint;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "Hadolint");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ReportRejectedException("A Hadolint report is a JSON array.");
        }

        var findings = document.RootElement.EnumerateArray().Select(issue =>
        {
            var code = issue.String("code") ?? "unknown";
            var file = JsonHelpers.RelativePath(issue.String("file") ?? "Dockerfile");
            return new Finding
            {
                Fingerprint = $"hadolint|{code}|{file}",
                RuleId = code,
                Severity = issue.String("level") switch
                {
                    "error" => Severity.High,
                    "warning" => Severity.Medium,
                    "info" => Severity.Low,
                    _ => Severity.Info,
                },
                Title = issue.String("message") ?? code,
                Location = $"{file}:{issue.Int("line") ?? 0}",
            };
        }).ToList();

        return new ParsedReport(findings, "hadolint", context.Meta("toolVersion") ?? "unknown", null, null, null, null);
    }
}

// SonarQube quality gate status (response of api/qualitygates/project_status). The gate
// verdict is SonarQube's; the Control Plane records it and requires it to be OK.
public sealed class SonarQualityGateReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.CodeQuality;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "SonarQube quality gate");
        var status = document.RootElement.Object("projectStatus")
            ?? throw new ReportRejectedException("The quality gate report has no projectStatus.");
        var gate = status.String("status");
        var failing = status.Array("conditions")
            .Where(c => c.String("status") == "ERROR")
            .Select(c => $"{c.String("metricKey")}={c.String("actualValue")} (threshold {c.String("errorThreshold")})")
            .ToList();

        return new ParsedReport([], "sonarqube", context.Meta("toolVersion") ?? "unknown", null, null,
            gate == "OK", gate == "OK" ? "quality gate OK" : $"quality gate {gate}: {string.Join("; ", failing)}");
    }
}
