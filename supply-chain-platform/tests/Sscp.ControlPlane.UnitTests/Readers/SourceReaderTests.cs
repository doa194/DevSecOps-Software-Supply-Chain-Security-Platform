// Source-evidence readers against real scanner output captured from the platform's own
// pipeline (Fixtures/). These pin down how each tool's format becomes findings: severity,
// repository-relative locations and fingerprints stable enough for risk exceptions.
using System.Text;
using Sscp.ControlPlane.Application.Evidence.Readers;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.UnitTests.Readers;

public sealed class SourceReaderTests
{
    private static readonly ReportContext Context = new(TestData.Commit, null, null, new Dictionary<string, string>());

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Semgrep_findings_take_the_rule_default_level_and_repository_relative_paths()
    {
        var report = new SarifReader().Parse(Fixture("semgrep-1.178.0-commerce.sarif"), Context);

        Assert.Equal("1.178.0", report.ToolVersion);
        Assert.Equal(5, report.Findings.Count);
        Assert.All(report.Findings, f =>
        {
            Assert.Equal("sscp.dotnet.anonymous-endpoint", f.RuleId);
            Assert.Equal(Severity.Low, f.Severity);
            Assert.StartsWith("src/", f.Location, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Semgrep_fingerprints_distinguish_matches_in_the_same_file_but_ignore_line_moves()
    {
        var original = new SarifReader().Parse(Fixture("semgrep-1.178.0-commerce.sarif"), Context);
        var shifted = new SarifReader().Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture("semgrep-1.178.0-commerce.sarif")).Replace("\"startLine\": 28", "\"startLine\": 40", StringComparison.Ordinal)),
            Context);

        Assert.Equal(original.Findings.Count, original.Findings.Select(f => f.Fingerprint).Distinct().Count());
        Assert.Equal(original.Findings.Select(f => f.Fingerprint), shifted.Findings.Select(f => f.Fingerprint));
    }

    [Fact]
    public void Checkov_failed_checks_are_high_unless_the_policy_downgrades_them()
    {
        var overrides = new Dictionary<string, Severity> { ["CKV_DOCKER_2"] = Severity.Low };

        var report = new CheckovReader(overrides).Parse(Fixture("checkov-3.3.19-dockerfile.json"), Context);

        Assert.Equal("3.3.19", report.ToolVersion);
        Assert.Equal(Severity.Low, report.Findings.Single(f => f.RuleId == "CKV_DOCKER_2").Severity);
        Assert.Equal(Severity.High, report.Findings.Single(f => f.RuleId == "CKV_DOCKER_7").Severity);
        Assert.Equal("checkov|CKV_DOCKER_7|Dockerfile|/Dockerfile.FROM", report.Findings.Single(f => f.RuleId == "CKV_DOCKER_7").Fingerprint);
    }

    [Fact]
    public void Checkov_checks_skipped_by_an_inline_comment_still_count_as_failed()
    {
        // Captured from a Dockerfile with `# checkov:skip=CKV_DOCKER_3: ...` above its RUN line.
        var report = new CheckovReader(new Dictionary<string, Severity>()).Parse(Fixture("checkov-3.3.19-inline-skip.json"), Context);

        Assert.Equal(Severity.High, report.Findings.Single(f => f.RuleId == "CKV_DOCKER_3").Severity);
    }

    [Fact]
    public void Hadolint_levels_map_to_severities_with_relative_file_names()
    {
        var finding = new HadolintReader().Parse(Fixture("hadolint-2.15.1-commerce.json"), Context).Findings.Single();

        Assert.Equal("DL3066", finding.RuleId);
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Equal("Dockerfile:42", finding.Location);
    }

    [Fact]
    public void Clean_gitleaks_report_has_no_findings()
    {
        Assert.Empty(new GitleaksReader().Parse(Fixture("gitleaks-8.30.1-clean.json"), Context).Findings);
    }

    [Fact]
    public void Unredacted_gitleaks_report_is_refused_so_secrets_never_reach_the_evidence_store()
    {
        var unredacted = Encoding.UTF8.GetBytes("""[{"RuleID":"generic-api-key","File":"/work/src/a.cs","StartLine":3,"Secret":"sk_live_abc123"}]""");

        Assert.Throws<ReportRejectedException>(() => new GitleaksReader().Parse(unredacted, Context));
    }
}
