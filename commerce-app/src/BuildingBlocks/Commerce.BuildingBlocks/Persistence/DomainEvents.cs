// In-process dispatch of domain events to their handlers (see ModuleDbContext).
using System.Collections.Concurrent;
using Commerce.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.BuildingBlocks.Persistence;

public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}

public interface IDomainEventDispatcher
{
    Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken);
}

internal sealed class DomainEventDispatcher(IServiceProvider services) : IDomainEventDispatcher
{
    // One small typed invoker per event type, created once, so dispatch needs neither
    // reflection per call nor dynamic typing.
    private static readonly ConcurrentDictionary<Type, Invoker> Invokers = new();

    public Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var invoker = Invokers.GetOrAdd(
            domainEvent.GetType(),
            type => (Invoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(type))!);
        return invoker.InvokeAsync(domainEvent, services, cancellationToken);
    }

    private abstract class Invoker
    {
        public abstract Task InvokeAsync(IDomainEvent domainEvent, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TEvent> : Invoker
        where TEvent : IDomainEvent
    {
        public override async Task InvokeAsync(IDomainEvent domainEvent, IServiceProvider services, CancellationToken cancellationToken)
        {
            foreach (var handler in services.GetServices<IDomainEventHandler<TEvent>>())
            {
                await handler.HandleAsync((TEvent)domainEvent, cancellationToken);
            }
        }
    }
}
