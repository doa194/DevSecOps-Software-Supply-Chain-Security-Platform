// Public contract of the Customers module.
using Commerce.SharedKernel.Classification;
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Customers.Contracts;

[IntegrationEvent("customers.customer-registered", 1)]
public sealed record CustomerRegisteredV1 : IntegrationEvent
{
    public required Guid CustomerId { get; init; }
    public required string UserSubject { get; init; }

    // Needed by the notification worker to address messages; classified so it is
    // redacted wherever it is logged.
    [PersonalData]
    public required string Email { get; init; }

    [PersonalData]
    public required string DisplayName { get; init; }
}

public sealed record CustomerReference(Guid CustomerId, string Tier);

public interface ICustomerDirectory
{
    Task<CustomerReference?> FindBySubjectAsync(string userSubject, CancellationToken cancellationToken);
}
