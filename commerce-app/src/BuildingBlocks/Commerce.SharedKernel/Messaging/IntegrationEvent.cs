// Integration events are the public, versioned messages a module publishes for other
// modules and workers. They travel through the transactional outbox and RabbitMQ and
// are signed by the publisher, so their shape is a contract that must stay stable.
namespace Commerce.SharedKernel.Messaging;

public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

// Gives every integration event a stable wire name, for example "orders.order-placed".
// The CLR type name is never used on the wire, so classes can be renamed safely.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute(string name, int version) : Attribute
{
    public string Name { get; } = name;
    public int Version { get; } = version;

    // Routing key used on the broker, for example "orders.order-placed.v1".
    public string RoutingKey => $"{Name}.v{Version}";
}
