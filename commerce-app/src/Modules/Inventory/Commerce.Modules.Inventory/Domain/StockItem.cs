// Inventory domain model: stock on hand per SKU and reservations held for orders.
//
// Available = OnHand - Reserved. A reservation is taken when an order is placed, released
// when it is cancelled, and committed (removed from stock) when it is confirmed.
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.Modules.Inventory.Domain;

public sealed record StockLevelChanged(string Sku, int OnHand, int Reserved, int Available, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed class Reservation
{
    public Guid OrderId { get; init; }
    public int Quantity { get; init; }
}

public sealed class StockItem : AggregateRoot<string>
{
    private readonly List<Reservation> _reservations = [];

    private StockItem() { }

    public int OnHand { get; private set; }
    public int LowStockThreshold { get; private set; }
    public IReadOnlyList<Reservation> Reservations => _reservations;
    public int Reserved => _reservations.Sum(r => r.Quantity);
    public int Available => OnHand - Reserved;
    public bool IsLow => Available <= LowStockThreshold;

    public static StockItem Track(string sku, int lowStockThreshold = 5) => new() { Id = sku, LowStockThreshold = lowStockThreshold };

    public Result Receive(int quantity, DateTimeOffset now)
    {
        if (quantity is <= 0 or > 100_000)
        {
            return Error.Validation("inventory.receipt.quantity", "Received quantity must be between 1 and 100000.");
        }

        OnHand += quantity;
        Changed(now);
        return Result.Success();
    }

    public bool CanReserve(int quantity) => quantity > 0 && Available >= quantity;

    public void Reserve(Guid orderId, int quantity, DateTimeOffset now)
    {
        if (_reservations.Any(r => r.OrderId == orderId))
        {
            return; // idempotent: the order already holds this reservation
        }

        if (!CanReserve(quantity))
        {
            throw new DomainException($"Cannot reserve {quantity} of {Id}; only {Available} available.");
        }

        _reservations.Add(new Reservation { OrderId = orderId, Quantity = quantity });
        Changed(now);
    }

    public void Release(Guid orderId, DateTimeOffset now)
    {
        if (_reservations.RemoveAll(r => r.OrderId == orderId) > 0)
        {
            Changed(now);
        }
    }

    public void Commit(Guid orderId, DateTimeOffset now)
    {
        var reservation = _reservations.FirstOrDefault(r => r.OrderId == orderId);
        if (reservation is null)
        {
            return;
        }

        _reservations.Remove(reservation);
        OnHand -= reservation.Quantity;
        Changed(now);
    }

    private void Changed(DateTimeOffset now)
    {
        Touch();
        Raise(new StockLevelChanged(Id, OnHand, Reserved, Available, now));
    }
}

// Reserves every line of an order or none of them. An order with one unavailable item
// must not leave stock locked for the others.
public static class ReservationService
{
    public sealed record Line(string Sku, int Quantity);

    public static Result Reserve(Guid orderId, IReadOnlyList<Line> lines, IReadOnlyDictionary<string, StockItem> stock, DateTimeOffset now)
    {
        var demand = lines.GroupBy(l => l.Sku, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity), StringComparer.Ordinal);
        var unavailable = demand
            .Where(pair => !stock.TryGetValue(pair.Key, out var item) || (!item.Reservations.Any(r => r.OrderId == orderId) && !item.CanReserve(pair.Value)))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unavailable.Count > 0)
        {
            return Error.Conflict("inventory.insufficient-stock", string.Join(",", unavailable));
        }

        foreach (var (sku, quantity) in demand)
        {
            stock[sku].Reserve(orderId, quantity, now);
        }

        return Result.Success();
    }
}
