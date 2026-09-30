// Catalog managers create products. New products start as drafts and are invisible to
// shoppers until activated.
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Catalog.Data;
using Commerce.Modules.Catalog.Domain;
using Commerce.SharedKernel.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Catalog.Features;

public static class CreateProduct
{
    public sealed record Request(string Sku, string Name, string Description, string Category, decimal Price, string Currency);

    public sealed class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Sku).NotEmpty().MaximumLength(12);
            RuleFor(r => r.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
            RuleFor(r => r.Description).MaximumLength(2000);
            RuleFor(r => r.Category).NotEmpty().MaximumLength(60);
            RuleFor(r => r.Currency).Must(Currencies.IsSupported).WithMessage("Currency must be EUR, USD or GBP.");
        }
    }

    public static void Map(RouteGroupBuilder catalog) =>
        catalog.MapPost("/products", HandleAsync)
            .RequireAuthorization(Permissions.CatalogWrite)
            .Validate<Request>()
            .WithName("CreateProduct");

    private static async Task<IResult> HandleAsync(Request request, CatalogDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var sku = Sku.Create(request.Sku);
        if (sku.IsFailure)
        {
            return sku.Error.ToProblem();
        }

        if (await db.Products.AnyAsync(p => p.Sku == sku.Value.Value, cancellationToken))
        {
            return SharedKernel.Results.Error.Conflict("catalog.sku.taken", "A product with this SKU already exists.").ToProblem();
        }

        var created = Product.Create(sku.Value, request.Name, request.Description, request.Category, new Money(request.Price, request.Currency), clock.GetUtcNow());
        if (created.IsFailure)
        {
            return created.Error.ToProblem();
        }

        db.Products.Add(created.Value);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Created($"/api/catalog/products/{created.Value.Id}", ProductDto.From(created.Value));
    }
}

public static class Currencies
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) { "EUR", "USD", "GBP" };

    public static bool IsSupported(string? currency) => currency is not null && Supported.Contains(currency);
}
