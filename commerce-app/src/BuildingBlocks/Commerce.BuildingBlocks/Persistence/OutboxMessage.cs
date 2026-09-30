// Rows of the transactional outbox and inbox. Every module schema contains both tables.
//
// Outbox: an integration event is written to the same database transaction as the
// business change that caused it. A background publisher later sends it to RabbitMQ,
// so an event is never lost (change committed, message not sent) and never invented
// (message sent, change rolled back).
//
// Inbox: records which messages a consumer has already processed. Brokers deliver "at
// least once", so the inbox turns duplicate deliveries into no-ops (idempotent consumer).
namespace Commerce.BuildingBlocks.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string RoutingKey { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceParent { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class InboxMessage
{
    public Guid MessageId { get; init; }
    public required string Consumer { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}
