// Product search criteria as a specification.
//
// Justification for the pattern: the storefront, staff screens and the pricing query
// combine the same filters (status, category, price range, text) in different ways. A
// specification keeps each rule in one place and composes them into a single expression
// that EF Core translates to SQL, instead of repeating ad-hoc Where clauses per endpoint.
using System.Linq.Expressions;

namespace Commerce.Modules.Catalog.Domain;

public sealed class ProductSearch
{
    private readonly List<Expression<Func<Product, bool>>> _criteria = [];

    public static ProductSearch VisibleToShoppers() => new ProductSearch().WithStatus(ProductStatus.Active);

    public ProductSearch WithStatus(ProductStatus status)
    {
        _criteria.Add(product => product.Status == status);
        return this;
    }

    public ProductSearch InCategory(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalised = category.Trim();
            _criteria.Add(product => product.Category == normalised);
        }

        return this;
    }

    public ProductSearch PricedBetween(decimal? minimum, decimal? maximum)
    {
        if (minimum is { } min)
        {
            _criteria.Add(product => product.Price.Amount >= min);
        }

        if (maximum is { } max)
        {
            _criteria.Add(product => product.Price.Amount <= max);
        }

        return this;
    }

    public ProductSearch Matching(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var term = text.Trim().ToUpperInvariant();
            // This expression is translated to SQL (upper(name) LIKE ...); the culture and
            // comparison overloads suggested by the analyzer cannot be translated.
#pragma warning disable CA1862, CA1304, CA1311
            _criteria.Add(product => product.Name.ToUpper().Contains(term) || product.Sku.Contains(term));
#pragma warning restore CA1862, CA1304, CA1311
        }

        return this;
    }

    public IQueryable<Product> ApplyTo(IQueryable<Product> products) =>
        _criteria.Aggregate(products, (query, criterion) => query.Where(criterion));

    // Used by unit tests and in-memory checks.
    public bool IsSatisfiedBy(Product product) => _criteria.All(criterion => criterion.Compile()(product));
}
