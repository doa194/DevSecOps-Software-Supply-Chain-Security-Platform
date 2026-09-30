// Handler contract and the inbox-protected processing of one integration event.
using Commerce.BuildingBlocks.Persistence;
using Commerce.SharedKernel.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.BuildingBlocks.Messaging;

public sealed record MessageContext(Guid MessageId, string Source, string? CorrelationId, int Attempt);

// Handlers change entities through their module's DbContext but do not call SaveChanges:
// the processor saves the handler's changes and the inbox record in one transaction.
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IntegrationEvent
{
    Task HandleAsync(TEvent integrationEvent, MessageContext context, CancellationToken cancellationToken);
}

public sealed record HandlerRegistration(IntegrationEventDescriptor Event, Type HandlerType, Type ContextType, string ConsumerName);

public enum ProcessingOutcome
{
    Processed,
    Duplicate,
}

internal static class InboxProcessor
{
    public static async Task<ProcessingOutcome> ProcessAsync(
        IServiceProvider services,
        HandlerRegistration registration,
        IntegrationEvent integrationEvent,
        MessageContext messageContext,
        CancellationToken cancellationToken)
    {
        var context = (ModuleDbContext)services.GetRequiredService(registration.ContextType);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var alreadyProcessed = await context.InboxMessages.AnyAsync(
            message => message.MessageId == messageContext.MessageId && message.Consumer == registration.ConsumerName,
            cancellationToken);
        if (alreadyProcessed)
        {
            return ProcessingOutcome.Duplicate;
        }

        context.InboxMessages.Add(new InboxMessage
        {
            MessageId = messageContext.MessageId,
            Consumer = registration.ConsumerName,
            Type = registration.Event.RoutingKey,
            ProcessedAt = DateTimeOffset.UtcNow,
        });

        var handler = services.GetRequiredService(registration.HandlerType);
        await HandlerInvoker.InvokeAsync(handler, integrationEvent, messageContext, cancellationToken);

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProcessingOutcome.Processed;
    }
}

internal static class HandlerInvoker
{
    public static Task InvokeAsync(object handler, IntegrationEvent integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        var invoker = (Invoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(integrationEvent.GetType()))!;
        return invoker.InvokeAsync(handler, integrationEvent, context, cancellationToken);
    }

    private abstract class Invoker
    {
        public abstract Task InvokeAsync(object handler, IntegrationEvent integrationEvent, MessageContext context, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TEvent> : Invoker
        where TEvent : IntegrationEvent
    {
        public override Task InvokeAsync(object handler, IntegrationEvent integrationEvent, MessageContext context, CancellationToken cancellationToken) =>
            ((IIntegrationEventHandler<TEvent>)handler).HandleAsync((TEvent)integrationEvent, context, cancellationToken);
    }
}
