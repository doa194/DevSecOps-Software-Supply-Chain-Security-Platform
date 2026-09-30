// Readers for dynamic evidence: the OWASP ZAP report and the deterministic security API
// test results produced against the candidate images in the security-test environment.
using System.Text.Json;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Application.Evidence.Readers;

// ZAP "traditional JSON" report. riskcode 3/2/1/0 = High/Medium/Low/Informational.
public sealed class ZapReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.DynamicScan;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "ZAP");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("site", out _))
        {
            throw new ReportRejectedException("The ZAP report has no site section.");
        }

        var findings = new List<Finding>();
        foreach (var alert in root.Array("site").SelectMany(site => site.Array("alerts")))
        {
            var plugin = alert.String("pluginid") ?? "0";
            var reference = alert.String("alertRef") ?? plugin;
            findings.Add(new Finding
            {
                Fingerprint = $"zap|{plugin}|{reference}",
                RuleId = $"zap-{plugin}",
                Severity = alert.Int("riskcode") switch
                {
                    3 => Severity.High,
                    2 => Severity.Medium,
                    1 => Severity.Low,
                    _ => Severity.Info,
                },
                Title = alert.String("alert") ?? alert.String("name") ?? plugin,
                Location = alert.Array("instances").FirstOrDefault().String("uri"),
            });
        }

        return new ParsedReport(findings, "zap", root.String("@version") ?? context.Meta("toolVersion") ?? "unknown", null, null, null, null);
    }
}

// Results of the platform's deterministic authorization/security API tests:
// { "schemaVersion": 1, "suite": "...", "results": [ { "name": "...", "passed": true, "detail": "..." } ] }
public sealed class SecurityTestsReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.SecurityTests;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "security test");
        var root = document.RootElement;
        if (root.Int("schemaVersion") != 1)
        {
            throw new ReportRejectedException("Unsupported security test report schema.");
        }

        var results = root.Array("results").ToList();
        if (results.Count == 0)
        {
            throw new ReportRejectedException("A security test report without results proves nothing.");
        }

        var failed = results.Where(r => !(r.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True))
            .Select(r => r.String("name") ?? "unnamed").ToList();
        return new ParsedReport([], root.String("suite") ?? "security-tests", context.Meta("toolVersion") ?? "1", null, null,
            failed.Count == 0, failed.Count == 0 ? $"{results.Count} tests passed" : $"{failed.Count} of {results.Count} failed: {string.Join(", ", failed)}");
    }
}
