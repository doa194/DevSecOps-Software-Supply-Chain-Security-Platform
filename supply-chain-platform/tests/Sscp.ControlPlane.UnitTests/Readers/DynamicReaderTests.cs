// Dynamic-evidence readers against real output of the platform's dynamic-security job
// (Fixtures/, main commit 925f914): the authenticated ZAP API scan and the platform's
// authorization suite.
using System.Text;
using Sscp.ControlPlane.Application.Evidence.Readers;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.UnitTests.Readers;

public sealed class DynamicReaderTests
{
    private static readonly ReportContext Context = new(TestData.Commit, null, null, new Dictionary<string, string>());

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Zap_alerts_become_findings_graded_by_zap_risk_code()
    {
        var report = new ZapReader().Parse(Fixture("zap-2.17.0-commerce-api-scan.json"), Context);

        Assert.Equal("2.17.0", report.ToolVersion);
        Assert.Equal(2, report.Findings.Count);
        Assert.All(report.Findings, f => Assert.Equal(Severity.Info, f.Severity));
        Assert.All(report.Findings, f => Assert.StartsWith("zap|", f.Fingerprint, StringComparison.Ordinal));
    }

    [Fact]
    public void Passing_authorization_suite_is_a_passed_gate()
    {
        var report = new SecurityTestsReader().Parse(Fixture("authorization-suite-commerce.json"), Context);

        Assert.True(report.GatePassed);
        Assert.Equal("25 tests passed", report.GateDetail);
    }

    [Fact]
    public void One_failed_authorization_check_fails_the_gate_and_names_it()
    {
        const string passed = "\"passed\": true";
        var text = Encoding.UTF8.GetString(Fixture("authorization-suite-commerce.json"));
        var first = text.IndexOf(passed, StringComparison.Ordinal);
        var firstCheckFailed = string.Concat(text.AsSpan(0, first), "\"passed\": false", text.AsSpan(first + passed.Length));

        var report = new SecurityTestsReader().Parse(Encoding.UTF8.GetBytes(firstCheckFailed), Context);

        Assert.False(report.GatePassed);
        Assert.Contains("anonymous users can browse the catalogue", report.GateDetail, StringComparison.Ordinal);
    }
}
