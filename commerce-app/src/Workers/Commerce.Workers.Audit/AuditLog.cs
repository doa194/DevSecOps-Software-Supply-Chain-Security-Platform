// The dedicated security and business audit trail.
//
// Entries are appended in a single, strictly ordered sequence and chained: each entry
// stores the SHA-256 of the previous entry together with its own content. Changing or
// deleting any entry after the fact breaks every hash that follows it, which the
// /api/audit/verify endpoint detects. In addition the worker's database role may only
// SELECT and INSERT in this schema, so the application itself cannot rewrite history.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commerce.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Commerce.Workers.Audit;

public sealed class AuditEntry
{
    public long Sequence { get; init; }
    public Guid EventId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public required string Source { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string Category { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceId { get; init; }
    public required string Outcome { get; init; }
    public required string Details { get; init; }
    public string? CorrelationId { get; init; }
    public required string PreviousHash { get; init; }
    public required string Hash { get; init; }
}

public static class AuditHashChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    // Canonical text of an entry: fixed field order, UTC timestamp at microsecond precision
    // (what PostgreSQL stores), details serialised with sorted keys.
    public static string Compute(string previousHash, Guid eventId, DateTimeOffset occurredAt, string source, string actor, string action,
        string category, string resourceType, string resourceId, string outcome, string details, string? correlationId)
    {
        var timestamp = occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var canonical = string.Join('\n',
            previousHash, eventId.ToString("D"), timestamp,
            source, actor, action, category, resourceType, resourceId, outcome, details, correlationId ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // PostgreSQL keeps microseconds; .NET keeps 100 ns ticks. Values are truncated before
    // hashing and storing so the stored row reproduces the same hash.
    public static DateTimeOffset ToStoragePrecision(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    public static string Recompute(AuditEntry entry) => Compute(entry.PreviousHash, entry.EventId, entry.OccurredAt, entry.Source, entry.Actor,
        entry.Action, entry.Category, entry.ResourceType, entry.ResourceId, entry.Outcome, entry.Details, entry.CorrelationId);

    public static string SerializeDetails(IReadOnlyDictionary<string, string> details) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(details.ToDictionary(), StringComparer.Ordinal));

    public sealed record VerificationResult(bool Intact, long EntriesChecked, long? FirstBrokenSequence, string? Problem);

    // Walks the chain in order; the first entry whose stored hash or link does not match is
    // reported. Everything after it is untrustworthy.
    public static VerificationResult Verify(IEnumerable<AuditEntry> orderedEntries)
    {
        var expectedPrevious = Genesis;
        long checkedCount = 0;
        foreach (var entry in orderedEntries)
        {
            checkedCount++;
            if (!string.Equals(entry.PreviousHash, expectedPrevious, StringComparison.Ordinal))
            {
                return new VerificationResult(false, checkedCount, entry.Sequence, "link to previous entry does not match (entry removed or reordered)");
            }

            if (!string.Equals(Recompute(entry), entry.Hash, StringComparison.Ordinal))
            {
                return new VerificationResult(false, checkedCount, entry.Sequence, "entry content does not match its hash (entry modified)");
            }

            expectedPrevious = entry.Hash;
        }

        return new VerificationResult(true, checkedCount, null, null);
    }
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, IServiceProvider services) : ModuleDbContext(options, services)
{
    public const string SchemaName = "audit";
    public override string Schema => SchemaName;

    public DbSet<AuditEntry> Entries => Set<AuditEntry>();
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> entry)
    {
        entry.ToTable("audit_log");
        entry.HasKey(e => e.Sequence);
        entry.Property(e => e.Sequence).UseIdentityAlwaysColumn();
        entry.HasIndex(e => e.EventId).IsUnique();
        entry.HasIndex(e => new { e.ResourceType, e.ResourceId });
        entry.HasIndex(e => e.Actor);
        entry.Property(e => e.Source).HasMaxLength(60);
        entry.Property(e => e.Actor).HasMaxLength(100);
        entry.Property(e => e.Action).HasMaxLength(100);
        entry.Property(e => e.Category).HasMaxLength(20);
        entry.Property(e => e.ResourceType).HasMaxLength(60);
        entry.Property(e => e.ResourceId).HasMaxLength(100);
        entry.Property(e => e.Outcome).HasMaxLength(20);
        // Plain text, not jsonb: jsonb re-formats the document, which would change the exact
        // bytes that were hashed and make every entry look tampered with.
        entry.Property(e => e.Details).HasColumnType("text");
        entry.Property(e => e.CorrelationId).HasMaxLength(100);
        entry.Property(e => e.PreviousHash).HasMaxLength(64);
        entry.Property(e => e.Hash).HasMaxLength(64);
    }
}

internal sealed class AuditDesignTimeFactory() : DesignTimeFactory<AuditDbContext>(AuditDbContext.SchemaName);
