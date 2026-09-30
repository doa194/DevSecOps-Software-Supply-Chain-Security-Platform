// Catalog domain model: a product with a validated SKU, a price and a lifecycle.
using System.Text.RegularExpressions;
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.Modules.Catalog.Domain;

public enum ProductStatus
{
    Draft,
    Active,
    Retired,
}

public sealed partial record Sku
{
    private Sku(string value) => Value = value;

    public string Value { get; }

    // Format: three upper-case letters, a dash, 4-6 digits (e.g. "KBD-1001").
    public static Result<Sku> Create(string? value) =>
        value is not null && Pattern().IsMatch(value)
            ? new Sku(value)
            : Error.Validation("catalog.sku.invalid", "SKU must look like ABC-1234.");

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Z]{3}-[0-9]{4,6}$")]
    private static partial Regex Pattern();
}

public sealed record ProductCreated(Guid ProductId, string Sku, string Name, Money Price, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record ProductPriceChanged(Guid ProductId, string Sku, Money OldPrice, Money NewPrice, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record ProductRetired(Guid ProductId, string Sku, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed class PriceHistoryEntry
{
    public Guid Id { get; init; }
    public decimal OldPrice { get; init; }
    public decimal NewPrice { get; init; }
    public required string ChangedBy { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
}

public sealed class Product : AggregateRoot<Guid>
{
    public const int MaxNameLength = 120;
    public const decimal MaxPrice = 100_000m;

    private readonly List<PriceHistoryEntry> _priceHistory = [];

    private Product() { }

    public string Sku { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public Money Price { get; private set; }
    public ProductStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<PriceHistoryEntry> PriceHistory => _priceHistory;

    public static Result<Product> Create(Sku sku, string name, string description, string category, Money price, DateTimeOffset now)
    {
        var priceCheck = ValidatePrice(price);
        if (priceCheck.IsFailure)
        {
            return priceCheck.Error;
        }

        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Sku = sku.Value,
            Name = name.Trim(),
            Description = description.Trim(),
            Category = category.Trim(),
            Price = price,
            Status = ProductStatus.Draft,
            CreatedAt = now,
        };
        product.Raise(new ProductCreated(product.Id, product.Sku, product.Name, price, now));
        return product;
    }

    public Result ChangePrice(Money newPrice, string changedBy, bool recordHistory, DateTimeOffset now)
    {
        if (Status == ProductStatus.Retired)
        {
            return Error.Conflict("catalog.product.retired", "A retired product cannot be repriced.");
        }

        if (!string.Equals(newPrice.Currency, Price.Currency, StringComparison.Ordinal))
        {
            return Error.Validation("catalog.price.currency", "The currency of a product cannot change.");
        }

        var priceCheck = ValidatePrice(newPrice);
        if (priceCheck.IsFailure || newPrice == Price)
        {
            return priceCheck;
        }

        if (recordHistory)
        {
            _priceHistory.Add(new PriceHistoryEntry { Id = Guid.CreateVersion7(), OldPrice = Price.Amount, NewPrice = newPrice.Amount, ChangedBy = changedBy, ChangedAt = now });
        }

        var oldPrice = Price;
        Price = newPrice;
        Touch();
        Raise(new ProductPriceChanged(Id, Sku, oldPrice, newPrice, now));
        return Result.Success();
    }

    public Result Activate()
    {
        if (Status == ProductStatus.Retired)
        {
            return Error.Conflict("catalog.product.retired", "A retired product cannot be re-activated.");
        }

        Status = ProductStatus.Active;
        Touch();
        return Result.Success();
    }

    public Result Retire(DateTimeOffset now)
    {
        if (Status == ProductStatus.Retired)
        {
            return Result.Success();
        }

        Status = ProductStatus.Retired;
        Touch();
        Raise(new ProductRetired(Id, Sku, now));
        return Result.Success();
    }

    private static Result ValidatePrice(Money price) =>
        price.Amount is > 0 and <= MaxPrice && decimal.Round(price.Amount, 2) == price.Amount
            ? Result.Success()
            : Error.Validation("catalog.price.invalid", $"Price must be between 0.01 and {MaxPrice} with at most two decimals.");
}
