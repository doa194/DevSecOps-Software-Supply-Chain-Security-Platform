// Table mapping for the outbox and inbox, applied to every module schema.
using Microsoft.EntityFrameworkCore;

namespace Commerce.BuildingBlocks.Persistence;

public static class OutboxModelConfiguration
{
    public static void ApplyOutboxAndInbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Type).HasMaxLength(200);
            outbox.Property(m => m.RoutingKey).HasMaxLength(200);
            outbox.Property(m => m.Payload).HasColumnType("jsonb");
            outbox.Property(m => m.CorrelationId).HasMaxLength(100);
            outbox.Property(m => m.TraceParent).HasMaxLength(100);
            outbox.Property(m => m.LastError).HasMaxLength(2000);
            // The publisher only ever looks for unprocessed rows in time order.
            outbox.HasIndex(m => m.OccurredAt).HasFilter("processed_at IS NULL");
        });

        modelBuilder.Entity<InboxMessage>(inbox =>
        {
            inbox.ToTable("inbox_messages");
            // The composite key is what makes consumers idempotent: a second insert of the
            // same message for the same consumer violates it and is treated as a duplicate.
            inbox.HasKey(m => new { m.MessageId, m.Consumer });
            inbox.Property(m => m.Consumer).HasMaxLength(200);
            inbox.Property(m => m.Type).HasMaxLength(200);
        });
    }
}
