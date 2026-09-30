// Product details. Anonymous callers see only active products; catalog staff also see
// drafts and retired products. Hidden products answer 404 so their existence is not leaked.
using Commerce.BuildingBlocks.Security;
using Commerce.Modules.Catalog.Data;
using Commerce.Modules.Catalog.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Catalog.Features;

public static class GetProduct
{
    public static void Map(RouteGroupBuilder catalog) =>
        catalog.MapGet("/products/{id:guid}", HandleAsync)
            .AllowAnonymous()
            .WithName("GetProduct");

    private static async Task<IResult> HandleAsync(Guid id, CatalogDbContext db, ICurrentUser user, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        var visible = product is not null && (product.Status == ProductStatus.Active || user.HasPermission(Permissions.CatalogWrite));
        return visible ? TypedResults.Ok(ProductDto.From(product!)) : TypedResults.NotFound();
    }
}
