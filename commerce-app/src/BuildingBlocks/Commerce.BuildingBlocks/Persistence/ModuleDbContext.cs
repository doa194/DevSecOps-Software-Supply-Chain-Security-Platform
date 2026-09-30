// Base class for every module's and worker's DbContext.
//
// - Each module owns exactly one PostgreSQL schema; its tables, its outbox, its inbox and
//   its migration history all live there, so ownership is visible in the database too.
// - Domain events raised by aggregates are dispatched to in-process handlers inside the
//   same SaveChanges call, before the transaction commits. Handlers typically translate a
//   domain event into an integration event (written to the outbox) and an audit record, so
//   all three are committed atomically.
using Commerce.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.BuildingBlocks.Persistence;

public abstract class ModuleDbContext(DbContextOptions options, IServiceProvider services) : DbContext(options)
{
    public abstract string Schema { get; }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyOutboxAndInbox();
        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly, type => type.Namespace?.StartsWith(GetType().Namespace!, StringComparison.Ordinal) == true);
        base.OnModelCreating(modelBuilder);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await DispatchDomainEventsAsync(cancellationToken);
        return await base.SaveChangesAsync(cancellationToken);
    }

    private async Task DispatchDomainEventsAsync(CancellationToken cancellationToken)
    {
        var dispatcher = services.GetService<IDomainEventDispatcher>();
        if (dispatcher is null)
        {
            return;
        }

        // Handlers may change more aggregates and raise more events, so keep draining
        // until no tracked aggregate has pending events.
        while (true)
        {
            var aggregates = ChangeTracker.Entries<IHasDomainEvents>()
                .Select(entry => entry.Entity)
                .Where(entity => entity.DomainEvents.Count > 0)
                .ToList();
            if (aggregates.Count == 0)
            {
                return;
            }

            var events = aggregates.SelectMany(aggregate => aggregate.DomainEvents).ToList();
            aggregates.ForEach(aggregate => aggregate.ClearDomainEvents());
            foreach (var domainEvent in events)
            {
                await dispatcher.DispatchAsync(domainEvent, cancellationToken);
            }
        }
    }
}
