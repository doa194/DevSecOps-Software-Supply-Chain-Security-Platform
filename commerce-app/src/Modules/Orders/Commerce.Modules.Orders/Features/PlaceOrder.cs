// Command: a customer places an order.
//
// Security-relevant rules:
// - Prices come from the catalog on the server; the request carries only SKUs and
//   quantities, so a client cannot choose its own price.
// - The customer id is resolved from the caller's token, never taken from the request.
// - An Idempotency-Key is required, so a retried request cannot create a second order.
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Catalog.Contracts;
using Commerce.Modules.Customers.Contracts;
using Commerce.Modules.Orders.Data;
using Commerce.Modules.Orders.Domain;
using Commerce.SharedKernel.Classification;
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Commerce.Modules.Orders.Features;

public static class PlaceOrder
{
    public sealed record LineRequest(string Sku, int Quantity);

    public sealed record Request(IReadOnlyList<LineRequest> Lines, [property: FinancialData] string PaymentMethodToken);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Lines).NotEmpty().Must(lines => lines.Count <= Order.MaxLines);
            RuleForEach(r => r.Lines).ChildRules(line =>
            {
                line.RuleFor(l => l.Sku).NotEmpty().MaximumLength(12);
                line.RuleFor(l => l.Quantity).InclusiveBetween(1, Order.MaxQuantityPerLine);
            });
            // Tokens from the simulated payment providers, e.g. card_approved_4242.
            RuleFor(r => r.PaymentMethodToken).NotEmpty().Matches("^(card|wallet)_[a-z0-9_]{3,40}$");
        }
    }

    public static void Map(RouteGroupBuilder orders) =>
        orders.MapPost("/", HandleAsync)
            .RequireAuthorization(Permissions.OrdersPlace)
            .Validate<Request>()
            .RequireIdempotencyKey()
            .WithName("PlaceOrder");

    private static async Task<IResult> HandleAsync(
        Request request,
        OrdersDbContext db,
        ICatalogQueries catalog,
        ICustomerDirectory customers,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var customer = await customers.FindBySubjectAsync(user.Subject!, cancellationToken);
        if (customer is null)
        {
            return Error.Conflict("orders.customer.unregistered", "Create a customer profile before placing orders.").ToProblem();
        }

        var skus = request.Lines.Select(l => l.Sku).Distinct(StringComparer.Ordinal).ToList();
        var prices = await catalog.GetActivePricesAsync(skus, cancellationToken);
        var unknown = skus.Where(sku => !prices.ContainsKey(sku)).ToList();
        if (unknown.Count > 0)
        {
            return Error.Validation("orders.products.unavailable", $"Not available: {string.Join(", ", unknown)}.").ToProblem();
        }

        var lines = request.Lines
            .Select(l => new PricedLine(l.Sku, prices[l.Sku].Name, l.Quantity, new Money(prices[l.Sku].Price, prices[l.Sku].Currency)))
            .ToList();
        var placed = Order.Place(customer.CustomerId, user.Subject!, lines, request.PaymentMethodToken, clock.GetUtcNow());
        if (placed.IsFailure)
        {
            return placed.Error.ToProblem();
        }

        db.Orders.Add(placed.Value);
        await db.SaveChangesAsync(cancellationToken);
        OrderMetrics.Placed.Add(1);
        return TypedResults.Created($"/api/orders/{placed.Value.Id}", OrderDto.From(placed.Value));
    }
}
