// Publishes customer registrations and answers "which customer is this user?" for Orders.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Customers.Contracts;
using Commerce.Modules.Customers.Data;
using Commerce.Modules.Customers.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Customers.Integration;

internal sealed class CustomerEventTranslation(Outbox<CustomersDbContext> outbox, AuditTrail<CustomersDbContext> audit) : IDomainEventHandler<CustomerRegistered>
{
    public Task HandleAsync(CustomerRegistered domainEvent, CancellationToken cancellationToken)
    {
        outbox.Add(new CustomerRegisteredV1
        {
            CustomerId = domainEvent.CustomerId,
            UserSubject = domainEvent.UserSubject,
            Email = domainEvent.Email,
            DisplayName = domainEvent.DisplayName,
        });
        audit.Record("customers.register", "customer", domainEvent.CustomerId.ToString());
        return Task.CompletedTask;
    }
}

internal sealed class CustomerDirectory(CustomersDbContext db) : ICustomerDirectory
{
    public async Task<CustomerReference?> FindBySubjectAsync(string userSubject, CancellationToken cancellationToken) =>
        await db.Customers.AsNoTracking()
            .Where(c => c.UserSubject == userSubject)
            .Select(c => new CustomerReference(c.Id, c.Tier.ToString()))
            .FirstOrDefaultAsync(cancellationToken);
}
