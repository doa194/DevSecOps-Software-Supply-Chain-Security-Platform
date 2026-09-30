// Base types for the domain model. Entities are identified by their Id; aggregate roots
// additionally collect domain events that are dispatched when the unit of work commits.
namespace Commerce.SharedKernel.Domain;

public abstract class Entity<TId>
    where TId : notnull
{
    protected Entity(TId id) => Id = id;

    // Needed by EF Core when it materialises entities from the database.
    protected Entity() => Id = default!;

    public TId Id { get; protected init; }
}

public abstract class AggregateRoot<TId> : Entity<TId>, IHasDomainEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) : base(id) { }
    protected AggregateRoot() { }

    // Mapped as a concurrency token: two concurrent updates of the same aggregate cannot
    // silently overwrite each other; the second one fails and must be retried.
    public int Version { get; protected set; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    // Called for every state change so the concurrency token moves even when a change
    // raises no event.
    protected void Touch() => Version++;

    public void ClearDomainEvents() => _domainEvents.Clear();
}
