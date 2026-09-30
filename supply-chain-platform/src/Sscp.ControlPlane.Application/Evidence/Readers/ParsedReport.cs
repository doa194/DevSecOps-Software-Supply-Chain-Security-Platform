// Common shape of a parsed scanner report.
//
// The Control Plane never trusts counts or verdicts sent by CI. Every raw report is parsed
// here into normalised findings, and each reader checks that the report really describes
// the commit or image digest the submission claims ("subject binding"). A report that
// cannot be parsed, or that describes something else, is refused.
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Application.Evidence.Readers;

public sealed record ReportContext(string Commit, string? Digest, string? Repository, IReadOnlyDictionary<string, string> Metadata)
{
    public string? Meta(string key) => Metadata.TryGetValue(key, out var value) ? value : null;
}

public sealed record ParsedReport(
    IReadOnlyList<Finding> Findings,
    string ToolName,
    string ToolVersion,
    string? DatabaseVersion,
    DateTimeOffset? DatabaseUpdatedAt,
    bool? GatePassed,
    string? GateDetail);

public sealed class ReportRejectedException(string reason) : Exception(reason);

public interface IEvidenceReader
{
    EvidenceKind Kind { get; }
    ParsedReport Parse(byte[] content, ReportContext context);
}

public sealed class EvidenceReaders(IEnumerable<IEvidenceReader> readers)
{
    private readonly Dictionary<EvidenceKind, IEvidenceReader> _byKind = readers.ToDictionary(r => r.Kind);

    public IEvidenceReader For(EvidenceKind kind) =>
        _byKind.TryGetValue(kind, out var reader) ? reader : throw new ReportRejectedException($"No reader is registered for {kind} evidence.");
}
