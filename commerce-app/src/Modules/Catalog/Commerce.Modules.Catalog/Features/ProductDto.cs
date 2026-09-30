// Read model returned by catalog endpoints.
using Commerce.Modules.Catalog.Domain;

namespace Commerce.Modules.Catalog.Features;

public sealed record ProductDto(Guid Id, string Sku, string Name, string Description, string Category, decimal Price, string Currency, string Status)
{
    public static ProductDto From(Product product) => new(
        product.Id, product.Sku, product.Name, product.Description, product.Category,
        product.Price.Amount, product.Price.Currency, product.Status.ToString());
}
