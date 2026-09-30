// Writes integration events into the current module's outbox table. The row is saved by
// the same SaveChanges call (and therefore the same transaction) as the business change.
using System.Diagnostics;
using System.Text.Json;
using Commerce.BuildingBlocks.Messaging;
using Commerce.SharedKernel.Messaging;

namespace Commerce.BuildingBlocks.Persistence;

public interface IOutbox
{
    void Add(IntegrationEvent integrationEvent);
}

public sealed class Outbox<TContext>(TContext context, ICorrelationContext correlation) : IOutbox
    where TContext : ModuleDbContext
{
    public void Add(IntegrationEvent integrationEvent)
    {
        var descriptor = IntegrationEventTypes.Describe(integrationEvent.GetType());
        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = integrationEvent.EventId,
            Type = descriptor.Name,
            RoutingKey = descriptor.RoutingKey,
            Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), MessagingJson.Options),
            OccurredAt = integrationEvent.OccurredAt,
            CorrelationId = correlation.CorrelationId,
            // Stored so the trace continues from the original request when the message is
            // published minutes later by the background publisher.
            TraceParent = Activity.Current?.Id,
        });
    }
}
