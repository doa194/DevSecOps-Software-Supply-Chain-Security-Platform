// The Control Plane's own audit log of supply-chain lifecycle events (build, evidence,
// decision, exception, signature, promotion, GitOps change, deployment).
//
// Like the workload's audit trail it is a hash chain: each entry commits to the previous
// entry's hash, so rewriting history is detectable by recomputing the chain.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sscp.ControlPlane.Domain.Auditing;

public sealed class AuditEntry
{
    public long Sequence { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public required string Details { get; init; }
    public string? CorrelationId { get; init; }
    public required string PreviousHash { get; init; }
    public required string Hash { get; init; }
}

public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    // PostgreSQL stores microseconds; values are truncated before hashing and storing.
    public static DateTimeOffset ToStoragePrecision(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    public static string Compute(string previousHash, DateTimeOffset occurredAt, string actor, string action, string subjectType, string subjectId, string details, string? correlationId)
    {
        var canonical = string.Join('\n',
            previousHash,
            occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            actor, action, subjectType, subjectId, details, correlationId ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public sealed record VerificationResult(bool Intact, long EntriesChecked, long? FirstBrokenSequence);

    public static VerificationResult Verify(IEnumerable<AuditEntry> ordered)
    {
        var previous = Genesis;
        long count = 0;
        foreach (var entry in ordered)
        {
            count++;
            var expected = Compute(entry.PreviousHash, entry.OccurredAt, entry.Actor, entry.Action, entry.SubjectType, entry.SubjectId, entry.Details, entry.CorrelationId);
            if (entry.PreviousHash != previous || entry.Hash != expected)
            {
                return new VerificationResult(false, count, entry.Sequence);
            }

            previous = entry.Hash;
        }

        return new VerificationResult(true, count, null);
    }
}
