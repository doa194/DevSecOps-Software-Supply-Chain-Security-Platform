// Response model for customer data. The classification attributes make the JSON
// serializer mask these fields for staff without personal-data clearance.
using Commerce.Modules.Customers.Domain;
using Commerce.SharedKernel.Classification;

namespace Commerce.Modules.Customers.Features;

public sealed record AddressDto(
    Guid Id,
    string Label,
    [property: PersonalData] string Line1,
    [property: PersonalData] string City,
    [property: PersonalData] string PostalCode,
    string Country,
    bool IsDefault);

public sealed record CustomerDto(
    Guid Id,
    [property: PersonalData] string DisplayName,
    [property: PersonalData] string Email,
    [property: PersonalData] string? Phone,
    string Tier,
    DateTimeOffset RegisteredAt,
    IReadOnlyList<AddressDto> Addresses)
{
    public static CustomerDto From(Customer customer) => new(
        customer.Id, customer.DisplayName, customer.Email, customer.Phone, customer.Tier.ToString(), customer.RegisteredAt,
        customer.Addresses.Select(a => new AddressDto(a.Id, a.Label, a.Line1, a.City, a.PostalCode, a.Country, a.IsDefault)).ToList());
}
