// Catalog managers reprice, activate and retire products. Price history is recorded only
// while the Catalog.PriceHistory feature flag is on.
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Administration.Contracts;
using Commerce.Modules.Catalog.Data;
using Commerce.SharedKernel.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;

namespace Commerce.Modules.Catalog.Features;

public static class ManageProduct
{
    public sealed record PriceRequest(decimal Price, string Currency);

    public sealed class PriceValidator : AbstractValidator<PriceRequest>
    {
        public PriceValidator() => RuleFor(r => r.Currency).Must(Currencies.IsSupported);
    }

    public static void Map(RouteGroupBuilder catalog)
    {
        catalog.MapPut("/products/{id:guid}/price", ChangePriceAsync)
            .RequireAuthorization(Permissions.CatalogWrite)
            .Validate<PriceRequest>()
            .WithName("ChangeProductPrice");
        catalog.MapPost("/products/{id:guid}/activate", ActivateAsync)
            .RequireAuthorization(Permissions.CatalogWrite)
            .WithName("ActivateProduct");
        catalog.MapPost("/products/{id:guid}/retire", RetireAsync)
            .RequireAuthorization(Permissions.CatalogWrite)
            .WithName("RetireProduct");
    }

    private static async Task<IResult> ChangePriceAsync(
        Guid id, PriceRequest request, CatalogDbContext db, ICurrentUser user, IFeatureManager features, TimeProvider clock, CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        var recordHistory = await features.IsEnabledAsync(FeatureFlags.CatalogPriceHistory, cancellationToken);
        var result = product.ChangePrice(new Money(request.Price, request.Currency), user.Actor, recordHistory, clock.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ProductDto.From(product));
    }

    private static async Task<IResult> ActivateAsync(Guid id, CatalogDbContext db, CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        var result = product.Activate();
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ProductDto.From(product));
    }

    private static async Task<IResult> RetireAsync(Guid id, CatalogDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return TypedResults.NotFound();
        }

        product.Retire(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ProductDto.From(product));
    }
}
