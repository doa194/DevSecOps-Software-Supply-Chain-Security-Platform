// Public storefront listing. One of the few anonymous endpoints: it exposes only active
// products and only public catalog data, and it is served from the Redis cache.
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Catalog.Data;
using Commerce.Modules.Catalog.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Catalog.Features;

public static class BrowseProducts
{
    public sealed record Query(string? Category, string? Search, decimal? MinPrice, decimal? MaxPrice, int Page = 1, int PageSize = 20);

    public static void Map(RouteGroupBuilder catalog) =>
        catalog.MapGet("/products", HandleAsync)
            .AllowAnonymous()
            .WithName("BrowseProducts");

    private static async Task<IResult> HandleAsync([AsParameters] Query query, CatalogDbContext db, ProductListCache cache, CancellationToken cancellationToken)
    {
        var paging = new PageRequest(query.Page, query.PageSize);
        var cacheKey = $"{query.Category}|{query.Search}|{query.MinPrice}|{query.MaxPrice}|{paging.SafePage}|{paging.SafePageSize}";
        var cached = await cache.GetAsync<PagedResult<ProductDto>>(cacheKey);
        if (cached is not null)
        {
            return TypedResults.Ok(cached);
        }

        var products = ProductSearch.VisibleToShoppers()
            .InCategory(query.Category)
            .Matching(query.Search)
            .PricedBetween(query.MinPrice, query.MaxPrice)
            .ApplyTo(db.Products.AsNoTracking());

        var total = await products.CountAsync(cancellationToken);
        var page = await products.OrderBy(p => p.Name).Skip(paging.Skip).Take(paging.SafePageSize).ToListAsync(cancellationToken);
        var result = new PagedResult<ProductDto>(page.Select(ProductDto.From).ToList(), paging.SafePage, paging.SafePageSize, total);
        await cache.SetAsync(cacheKey, result);
        return TypedResults.Ok(result);
    }
}
