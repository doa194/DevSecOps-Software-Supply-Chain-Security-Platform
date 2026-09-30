// Translates Catalog domain events into public integration events and audit records, and
// answers pricing queries from other modules.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Catalog.Contracts;
using Commerce.Modules.Catalog.Data;
using Commerce.Modules.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Catalog.Integration;

internal sealed class CatalogEventTranslation(Outbox<CatalogDbContext> outbox, AuditTrail<CatalogDbContext> audit, ProductListCache cache) :
    IDomainEventHandler<ProductCreated>,
    IDomainEventHandler<ProductPriceChanged>,
    IDomainEventHandler<ProductRetired>
{
    public async Task HandleAsync(ProductCreated domainEvent, CancellationToken cancellationToken)
    {
        outbox.Add(new ProductCreatedV1(domainEvent.ProductId, domainEvent.Sku, domainEvent.Name, domainEvent.Price.Amount, domainEvent.Price.Currency));
        audit.Record("catalog.product.create", "product", domainEvent.ProductId.ToString(), details: new Dictionary<string, string> { ["sku"] = domainEvent.Sku });
        await cache.InvalidateAsync();
    }

    public async Task HandleAsync(ProductPriceChanged domainEvent, CancellationToken cancellationToken)
    {
        outbox.Add(new ProductPriceChangedV1(domainEvent.ProductId, domainEvent.Sku, domainEvent.OldPrice.Amount, domainEvent.NewPrice.Amount, domainEvent.NewPrice.Currency));
        audit.Record("catalog.product.reprice", "product", domainEvent.ProductId.ToString(), details: new Dictionary<string, string>
        {
            ["sku"] = domainEvent.Sku,
            ["old"] = domainEvent.OldPrice.ToString(),
            ["new"] = domainEvent.NewPrice.ToString(),
        });
        await cache.InvalidateAsync();
    }

    public async Task HandleAsync(ProductRetired domainEvent, CancellationToken cancellationToken)
    {
        outbox.Add(new ProductRetiredV1(domainEvent.ProductId, domainEvent.Sku));
        audit.Record("catalog.product.retire", "product", domainEvent.ProductId.ToString());
        await cache.InvalidateAsync();
    }
}

internal sealed class CatalogQueries(CatalogDbContext db) : ICatalogQueries
{
    public async Task<IReadOnlyDictionary<string, CatalogPrice>> GetActivePricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        var products = await ProductSearch.VisibleToShoppers()
            .ApplyTo(db.Products.AsNoTracking())
            .Where(p => skus.Contains(p.Sku))
            .Select(p => new CatalogPrice(p.Sku, p.Name, p.Price.Amount, p.Price.Currency))
            .ToListAsync(cancellationToken);
        return products.ToDictionary(p => p.Sku, StringComparer.Ordinal);
    }
}
