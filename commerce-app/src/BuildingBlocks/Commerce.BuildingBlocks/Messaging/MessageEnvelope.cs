// The wire format of every integration message. The payload is kept as the exact JSON
// string that was signed, so verification never depends on re-serialising it.
namespace Commerce.BuildingBlocks.Messaging;

public sealed record MessageEnvelope
{
    public required Guid MessageId { get; init; }

    // Routing key of the event, for example "orders.order-placed.v1".
    public required string Type { get; init; }

    // Logical publisher, for example "commerce-api" or "document-worker". Must match the
    // publisher bound to the signing key, otherwise the message is rejected.
    public required string Source { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceParent { get; init; }
    public required string Payload { get; init; }
    public required string KeyId { get; init; }
    public required string Signature { get; init; }
}
