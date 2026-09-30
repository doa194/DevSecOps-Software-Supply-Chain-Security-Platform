// Domain events describe something that happened inside one module's model. They are
// handled in-process, inside the same transaction. Integration events (see Messaging)
// are the separate, versioned messages that other modules and workers receive.
namespace Commerce.SharedKernel.Domain;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}
