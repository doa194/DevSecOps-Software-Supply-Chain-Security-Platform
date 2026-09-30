// Image-evidence readers against real output of the platform's own main pipeline
// (Fixtures/, commerce-api built from commit 75d2a80). Each report must prove it describes
// the submitted digest; a report for another image is refused.
using Sscp.ControlPlane.Application.Evidence.Readers;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.UnitTests.Readers;

public sealed class ArtifactReaderTests
{
    private const string Digest = "sha256:8e0d4ed2fe44f0a52d0aef35338ab43b68f3b3c908baedcb85a3bdc97330b0b0";
    private const string OtherDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000001";

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ReportContext For(string digest, Dictionary<string, string>? metadata = null) =>
        new(TestData.Commit, digest, null, metadata ?? []);

    [Fact]
    public void Syft_sbom_is_bound_to_the_pushed_digest_and_lists_its_components()
    {
        var report = new CycloneDxReader().Parse(Fixture("syft-1.52.0-commerce-api.cdx.json"), For(Digest));

        Assert.True(report.GatePassed);
        Assert.Contains("426 components", report.GateDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Trivy_report_yields_findings_with_package_identity_and_the_submitted_database_age()
    {
        var report = new TrivyReader().Parse(Fixture("trivy-0.74.0-commerce-api.json"),
            For(Digest, new() { ["databaseVersion"] = "2", ["databaseUpdatedAt"] = "2026-09-27T06:12:48Z" }));

        Assert.Equal("0.74.0", report.ToolVersion);
        Assert.Equal(8, report.Findings.Count);
        Assert.All(report.Findings, f => Assert.Equal(Severity.Medium, f.Severity));
        Assert.All(report.Findings, f => Assert.StartsWith("CVE-", f.Fingerprint, StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Fingerprint.Contains("|pkg:deb/ubuntu/", StringComparison.Ordinal));
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 6, 12, 48, TimeSpan.Zero), report.DatabaseUpdatedAt);
    }

    [Fact]
    public void Grype_report_reads_its_own_database_build_time()
    {
        var report = new GrypeReader().Parse(Fixture("grype-0.119.0-commerce-api.json"), For(Digest));

        Assert.Equal("0.119.0", report.ToolVersion);
        Assert.Equal(12, report.Findings.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 6, 30, 30, TimeSpan.Zero), report.DatabaseUpdatedAt);
    }

    [Theory]
    [InlineData("syft-1.52.0-commerce-api.cdx.json")]
    [InlineData("trivy-0.74.0-commerce-api.json")]
    [InlineData("grype-0.119.0-commerce-api.json")]
    public void Report_for_another_image_is_refused(string fixture)
    {
        IEvidenceReader reader = fixture switch
        {
            _ when fixture.StartsWith("syft", StringComparison.Ordinal) => new CycloneDxReader(),
            _ when fixture.StartsWith("trivy", StringComparison.Ordinal) => new TrivyReader(),
            _ => new GrypeReader(),
        };

        Assert.Throws<ReportRejectedException>(() => reader.Parse(Fixture(fixture), For(OtherDigest)));
    }

    [Fact]
    public void SonarQube_gate_status_becomes_the_code_quality_verdict()
    {
        var report = new SonarQualityGateReader().Parse(Fixture("sonarqube-26.9-quality-gate.json"), For(Digest));

        Assert.True(report.GatePassed);
    }
}
