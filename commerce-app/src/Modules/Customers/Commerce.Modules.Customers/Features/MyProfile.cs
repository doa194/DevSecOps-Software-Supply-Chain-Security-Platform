// Self-service for the signed-in customer: register a profile, read it, manage addresses.
// The identity (subject and e-mail) always comes from the validated token, never from the
// request body, so nobody can register a profile for someone else.
using Commerce.BuildingBlocks.Classification;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Customers.Data;
using Commerce.Modules.Customers.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Customers.Features;

public static class MyProfile
{
    public sealed record RegisterRequest(string DisplayName, string? Phone);

    public sealed record AddressRequest(string Label, string Line1, string City, string PostalCode, string Country, bool MakeDefault);

    public sealed class RegisterValidator : AbstractValidator<RegisterRequest>
    {
        public RegisterValidator()
        {
            RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(100);
            RuleFor(r => r.Phone).MaximumLength(30).Matches("^[+0-9 ()-]*$");
        }
    }

    public sealed class AddressValidator : AbstractValidator<AddressRequest>
    {
        public AddressValidator()
        {
            RuleFor(r => r.Label).NotEmpty().MaximumLength(40);
            RuleFor(r => r.Line1).NotEmpty().MaximumLength(200);
            RuleFor(r => r.City).NotEmpty().MaximumLength(100);
            RuleFor(r => r.PostalCode).NotEmpty().MaximumLength(20);
            RuleFor(r => r.Country).NotEmpty().Length(2);
        }
    }

    public static void Map(RouteGroupBuilder customers)
    {
        var me = customers.MapGroup("/me").RequireAuthorization(Permissions.OrdersPlace);
        me.MapPost("/", RegisterAsync).Validate<RegisterRequest>().WithName("RegisterCustomer");
        me.MapGet("/", GetAsync).WithName("GetMyProfile");
        me.MapPost("/addresses", AddAddressAsync).Validate<AddressRequest>().WithName("AddAddress");
        me.MapDelete("/addresses/{addressId:guid}", RemoveAddressAsync).WithName("RemoveAddress");
    }

    private static async Task<IResult> RegisterAsync(RegisterRequest request, CustomersDbContext db, ICurrentUser user, HttpContext http, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await db.Customers.AnyAsync(c => c.UserSubject == user.Subject, cancellationToken))
        {
            return SharedKernel.Results.Error.Conflict("customers.already-registered", "A profile already exists for this account.").ToProblem();
        }

        var email = http.User.FindFirst("email")?.Value ?? string.Empty;
        var registered = Customer.Register(user.Subject!, request.DisplayName, email, request.Phone, clock.GetUtcNow());
        if (registered.IsFailure)
        {
            return registered.Error.ToProblem();
        }

        db.Customers.Add(registered.Value);
        await db.SaveChangesAsync(cancellationToken);
        DataClearance.GrantOwnerView(http);
        return TypedResults.Created("/api/customers/me", CustomerDto.From(registered.Value));
    }

    private static async Task<IResult> GetAsync(CustomersDbContext db, ICurrentUser user, HttpContext http, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.UserSubject == user.Subject, cancellationToken);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        DataClearance.GrantOwnerView(http);
        return TypedResults.Ok(CustomerDto.From(customer));
    }

    private static async Task<IResult> AddAddressAsync(AddressRequest request, CustomersDbContext db, ICurrentUser user, HttpContext http, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.UserSubject == user.Subject, cancellationToken);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        var added = customer.AddAddress(request.Label, request.Line1, request.City, request.PostalCode, request.Country, request.MakeDefault);
        if (added.IsFailure)
        {
            return added.Error.ToProblem();
        }

        await db.SaveChangesAsync(cancellationToken);
        DataClearance.GrantOwnerView(http);
        return TypedResults.Ok(CustomerDto.From(customer));
    }

    private static async Task<IResult> RemoveAddressAsync(Guid addressId, CustomersDbContext db, ICurrentUser user, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.UserSubject == user.Subject, cancellationToken);
        if (customer is null)
        {
            return TypedResults.NotFound();
        }

        var removed = customer.RemoveAddress(addressId);
        if (removed.IsFailure)
        {
            return removed.Error.ToProblem();
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
