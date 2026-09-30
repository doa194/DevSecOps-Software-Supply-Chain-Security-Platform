// Public contract of the Catalog module.
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Catalog.Contracts;

[IntegrationEvent("catalog.product-created", 1)]
public sealed record ProductCreatedV1(Guid ProductId, string Sku, string Name, decimal Price, string Currency) : IntegrationEvent;

[IntegrationEvent("catalog.product-price-changed", 1)]
public sealed record ProductPriceChangedV1(Guid ProductId, string Sku, decimal OldPrice, decimal NewPrice, string Currency) : IntegrationEvent;

[IntegrationEvent("catalog.product-retired", 1)]
public sealed record ProductRetiredV1(Guid ProductId, string Sku) : IntegrationEvent;

public sealed record CatalogPrice(string Sku, string Name, decimal Price, string Currency);

// In-process query used by Orders to price order lines on the server. Prices sent by a
// client are never trusted.
public interface ICatalogQueries
{
    Task<IReadOnlyDictionary<string, CatalogPrice>> GetActivePricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken);
}
