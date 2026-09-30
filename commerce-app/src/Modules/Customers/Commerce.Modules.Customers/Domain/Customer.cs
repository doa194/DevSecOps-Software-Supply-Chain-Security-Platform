// Customer domain model. Nearly every field here is personal data and is classified as
// such, which drives log redaction and response masking everywhere it travels.
using Commerce.SharedKernel.Classification;
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.Modules.Customers.Domain;

public enum CustomerTier
{
    Standard,
    Gold,
}

public sealed record CustomerRegistered(Guid CustomerId, string UserSubject, string Email, string DisplayName, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed class Address
{
    public Guid Id { get; init; }
    public required string Label { get; init; }
    [PersonalData] public required string Line1 { get; init; }
    [PersonalData] public required string City { get; init; }
    [PersonalData] public required string PostalCode { get; init; }
    public required string Country { get; init; }
    public bool IsDefault { get; set; }
}

public sealed class Customer : AggregateRoot<Guid>
{
    public const int MaxAddresses = 5;
    private readonly List<Address> _addresses = [];

    private Customer() { }

    public string UserSubject { get; private set; } = string.Empty;
    [PersonalData] public string DisplayName { get; private set; } = string.Empty;
    [PersonalData] public string Email { get; private set; } = string.Empty;
    [PersonalData] public string? Phone { get; private set; }
    public CustomerTier Tier { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }
    public IReadOnlyList<Address> Addresses => _addresses;

    public static Result<Customer> Register(string userSubject, string displayName, string email, string? phone, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(userSubject) || string.IsNullOrWhiteSpace(email))
        {
            return Error.Validation("customers.identity-missing", "A customer needs an authenticated identity with an e-mail address.");
        }

        var customer = new Customer
        {
            Id = Guid.CreateVersion7(),
            UserSubject = userSubject,
            DisplayName = displayName.Trim(),
            Email = email.Trim().ToLowerInvariant(),
            Phone = phone?.Trim(),
            Tier = CustomerTier.Standard,
            RegisteredAt = now,
        };
        customer.Raise(new CustomerRegistered(customer.Id, userSubject, customer.Email, customer.DisplayName, now));
        return customer;
    }

    public Result UpdateContact(string displayName, string? phone)
    {
        DisplayName = displayName.Trim();
        Phone = phone?.Trim();
        Touch();
        return Result.Success();
    }

    public Result<Address> AddAddress(string label, string line1, string city, string postalCode, string country, bool makeDefault)
    {
        if (_addresses.Count >= MaxAddresses)
        {
            return Error.Conflict("customers.addresses.limit", $"A customer can store at most {MaxAddresses} addresses.");
        }

        var address = new Address { Id = Guid.CreateVersion7(), Label = label, Line1 = line1, City = city, PostalCode = postalCode, Country = country.ToUpperInvariant() };
        _addresses.Add(address);
        // Exactly one default address whenever at least one address exists.
        if (makeDefault || _addresses.Count == 1)
        {
            SetDefault(address.Id);
        }

        Touch();
        return address;
    }

    public Result RemoveAddress(Guid addressId)
    {
        var address = _addresses.FirstOrDefault(a => a.Id == addressId);
        if (address is null)
        {
            return Error.NotFound("customers.address.not-found", "Address not found.");
        }

        _addresses.Remove(address);
        if (address.IsDefault && _addresses.Count > 0)
        {
            _addresses[0].IsDefault = true;
        }

        Touch();
        return Result.Success();
    }

    private void SetDefault(Guid addressId)
    {
        foreach (var address in _addresses)
        {
            address.IsDefault = address.Id == addressId;
        }
    }
}
