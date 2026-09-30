// Appends one audit record to the chain. An advisory lock serialises appends (across
// replicas too) so every entry links to exactly one predecessor.
using Commerce.BuildingBlocks.Messaging;
using Commerce.SharedKernel.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Workers.Audit;

internal sealed class AppendAuditEntry(AuditDbContext db, TimeProvider clock) : IIntegrationEventHandler<AuditRecordedV1>
{
    private const long ChainLockKey = 0x5553_4550_4155_4449; // arbitrary constant: "audit chain"

    public async Task HandleAsync(AuditRecordedV1 record, MessageContext context, CancellationToken cancellationToken)
    {
        // Runs inside the inbox transaction, so the lock is held until commit.
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ChainLockKey})", cancellationToken);

        var previous = await db.Entries.OrderByDescending(e => e.Sequence).Select(e => e.Hash).FirstOrDefaultAsync(cancellationToken)
            ?? AuditHashChain.Genesis;
        var details = AuditHashChain.SerializeDetails(record.Details);
        var occurredAt = AuditHashChain.ToStoragePrecision(record.OccurredAt);
        var hash = AuditHashChain.Compute(previous, record.EventId, occurredAt, context.Source, record.Actor, record.Action,
            record.Category, record.ResourceType, record.ResourceId, record.Outcome, details, record.CorrelationId);

        db.Entries.Add(new AuditEntry
        {
            EventId = record.EventId,
            OccurredAt = occurredAt,
            RecordedAt = clock.GetUtcNow(),
            Source = context.Source,
            Actor = record.Actor,
            Action = record.Action,
            Category = record.Category,
            ResourceType = record.ResourceType,
            ResourceId = record.ResourceId,
            Outcome = record.Outcome,
            Details = details,
            CorrelationId = record.CorrelationId,
            PreviousHash = previous,
            Hash = hash,
        });
    }
}
