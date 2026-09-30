// Readers for image-level evidence: the Syft CycloneDX SBOM and the Trivy and Grype
// vulnerability reports. Each must prove it describes the submitted digest.
using System.Text.Json;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Application.Evidence.Readers;

internal static class Binding
{
    public static void RequireDigest(string? actual, ReportContext context, string format)
    {
        if (context.Digest is null || actual is null || !actual.Contains(context.Digest, StringComparison.Ordinal))
        {
            throw new ReportRejectedException($"The {format} report describes '{actual ?? "nothing"}', not the submitted digest {context.Digest}.");
        }
    }
}

// CycloneDX JSON produced by Syft. The SBOM's top-level component must be the image digest,
// and an SBOM that lists no components is not accepted as evidence of what ships.
public sealed class CycloneDxReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.Sbom;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "CycloneDX");
        var root = document.RootElement;
        if (root.String("bomFormat") != "CycloneDX")
        {
            throw new ReportRejectedException("The SBOM is not a CycloneDX document.");
        }

        var component = root.Object("metadata")?.Object("component");
        var subject = $"{component?.String("name")}@{component?.String("version")}";
        Binding.RequireDigest(subject, context, "SBOM");

        var tool = root.Object("metadata")?.Object("tools")?.Array("components").FirstOrDefault();
        var components = root.Array("components").Count();
        return new ParsedReport([], tool?.String("name") ?? "syft", tool?.String("version") ?? context.Meta("toolVersion") ?? "unknown", null, null,
            components > 0, $"CycloneDX {root.String("specVersion")} with {components} components for {subject}");
    }
}

// Trivy image report (JSON). Trivy is the blocking vulnerability scanner. The database
// timestamp comes from `trivy version` output submitted alongside, so a scan made with a
// stale or missing database cannot pass the freshness rule.
public sealed class TrivyReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.VulnerabilityScan;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "Trivy");
        var root = document.RootElement;
        if (root.Int("SchemaVersion") is null)
        {
            throw new ReportRejectedException("The Trivy report has no SchemaVersion.");
        }

        var digests = root.Object("Metadata")?.Array("RepoDigests").Select(d => d.GetString()).ToList() ?? [];
        Binding.RequireDigest(string.Join(",", digests.Append(root.String("ArtifactName"))), context, "Trivy");

        var findings = new List<Finding>();
        foreach (var vulnerability in root.Array("Results").SelectMany(result => result.Array("Vulnerabilities")))
        {
            var id = vulnerability.String("VulnerabilityID") ?? "unknown";
            var package = vulnerability.Object("PkgIdentifier")?.String("PURL")
                ?? $"{vulnerability.String("PkgName")}@{vulnerability.String("InstalledVersion")}";
            var fixedVersion = vulnerability.String("FixedVersion");
            findings.Add(new Finding
            {
                Fingerprint = $"{id}|{package}",
                RuleId = id,
                Severity = JsonHelpers.ParseSeverity(vulnerability.String("Severity")),
                Title = vulnerability.String("Title") ?? id,
                Package = vulnerability.String("PkgName"),
                InstalledVersion = vulnerability.String("InstalledVersion"),
                FixedVersion = fixedVersion,
                FixAvailable = !string.IsNullOrEmpty(fixedVersion),
            });
        }

        return new ParsedReport(findings, "trivy", root.Object("Trivy")?.String("Version") ?? context.Meta("toolVersion") ?? "unknown",
            context.Meta("databaseVersion"), JsonHelpers.Timestamp(context.Meta("databaseUpdatedAt")), null, null);
    }
}

// Grype report (JSON) for the pushed image. Secondary evidence: its findings are stored and
// reported, but the vulnerability gate is decided on Trivy's results. The report must name
// the submitted digest (source.target.manifestDigest or the reference that was scanned).
public sealed class GrypeReader : IEvidenceReader
{
    public EvidenceKind Kind => EvidenceKind.SecondaryVulnerabilityScan;

    public ParsedReport Parse(byte[] content, ReportContext context)
    {
        using var document = JsonHelpers.Parse(content, "Grype");
        var root = document.RootElement;
        var descriptor = root.Object("descriptor") ?? throw new ReportRejectedException("The Grype report has no descriptor.");
        var target = root.Object("source")?.Object("target");
        Binding.RequireDigest($"{target?.String("manifestDigest")},{target?.String("userInput")}", context, "Grype");

        var findings = root.Array("matches").Select(match =>
        {
            var vulnerability = match.Object("vulnerability");
            var artifact = match.Object("artifact");
            var id = vulnerability?.String("id") ?? "unknown";
            var fixState = vulnerability?.Object("fix")?.String("state");
            return new Finding
            {
                Fingerprint = $"{id}|{artifact?.String("purl") ?? artifact?.String("name")}",
                RuleId = id,
                Severity = JsonHelpers.ParseSeverity(vulnerability?.String("severity")),
                Title = id,
                Package = artifact?.String("name"),
                InstalledVersion = artifact?.String("version"),
                FixedVersion = vulnerability?.Object("fix")?.Array("versions").FirstOrDefault().ValueKind == JsonValueKind.String
                    ? vulnerability?.Object("fix")?.Array("versions").First().GetString()
                    : null,
                FixAvailable = fixState == "fixed",
            };
        }).ToList();

        var db = descriptor.Object("db");
        var built = db?.Object("status")?.String("built") ?? db?.String("built");
        return new ParsedReport(findings, "grype", descriptor.String("version") ?? "unknown",
            db?.Object("status")?.String("schemaVersion") ?? context.Meta("databaseVersion"), JsonHelpers.Timestamp(built), null, null);
    }
}
