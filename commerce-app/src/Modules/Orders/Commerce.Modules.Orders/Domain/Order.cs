// Order aggregate and its state machine.
//
//   Placed ──stock reserved──▶ AwaitingPayment ──payment captured──▶ Confirmed ──▶ Fulfilled
//     │                           │                                     │
//     │ stock unavailable         │ payment failed / cancelled          │ refunded (full)
//     ▼                           ▼                                     ▼
//   Rejected                   Cancelled                             Refunded
//
// Every transition is a method that checks the current state; an event arriving for an
// order in the wrong state (a duplicate or a late message) is rejected instead of
// corrupting the order.
using Commerce.SharedKernel.Classification;
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.Modules.Orders.Domain;

public enum OrderStatus
{
    Placed,
    AwaitingPayment,
    Confirmed,
    Fulfilled,
    Rejected,
    Cancelled,
    Refunded,
}

public sealed record OrderLine(string Sku, string Name, int Quantity, decimal UnitPrice)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

public sealed record PricedLine(string Sku, string Name, int Quantity, Money UnitPrice);

public sealed record OrderPlaced(Order Order, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record OrderAwaitingPayment(Order Order, string PaymentMethodToken, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record OrderConfirmed(Order Order, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record OrderRejected(Order Order, string Reason, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record OrderCancelled(Order Order, string Reason, bool StockWasReserved, string CancelledBy, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record OrderFulfilled(Order Order, string FulfilledBy, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record OrderRefunded(Order Order, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed class Order : AggregateRoot<Guid>
{
    public const int MaxLines = 20;
    public const int MaxQuantityPerLine = 100;

    private readonly List<OrderLine> _lines = [];

    private Order() { }

    public Guid CustomerId { get; private set; }

    // Keycloak subject of the customer who placed the order; the basis of ownership checks.
    public string OwnerSubject { get; private set; } = string.Empty;

    public OrderStatus Status { get; private set; }
    public string? StatusReason { get; private set; }
    public decimal Total { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public IReadOnlyList<OrderLine> Lines => _lines;
    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // Held only until payment is requested, then erased (data minimisation).
    [FinancialData]
    public string? PaymentMethodToken { get; private set; }

    public static Result<Order> Place(Guid customerId, string ownerSubject, IReadOnlyList<PricedLine> lines, string paymentMethodToken, DateTimeOffset now)
    {
        if (lines.Count is 0 or > MaxLines)
        {
            return Error.Validation("orders.lines.count", $"An order needs between 1 and {MaxLines} lines.");
        }

        if (lines.Any(l => l.Quantity is < 1 or > MaxQuantityPerLine))
        {
            return Error.Validation("orders.lines.quantity", $"Quantities must be between 1 and {MaxQuantityPerLine}.");
        }

        var currencies = lines.Select(l => l.UnitPrice.Currency).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count != 1)
        {
            return Error.Validation("orders.currency.mixed", "All items of an order must share one currency.");
        }

        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            CustomerId = customerId,
            OwnerSubject = ownerSubject,
            Status = OrderStatus.Placed,
            Currency = currencies[0],
            PaymentMethodToken = paymentMethodToken,
            PlacedAt = now,
            UpdatedAt = now,
        };
        order._lines.AddRange(lines.Select(l => new OrderLine(l.Sku, l.Name, l.Quantity, l.UnitPrice.Amount)));
        order.Total = order._lines.Sum(l => l.LineTotal);
        order.Raise(new OrderPlaced(order, now));
        return order;
    }

    public Result MarkStockReserved(DateTimeOffset now)
    {
        var check = RequireStatus(OrderStatus.Placed);
        if (check.IsFailure)
        {
            return check;
        }

        var token = PaymentMethodToken!;
        PaymentMethodToken = null;
        Transition(OrderStatus.AwaitingPayment, null, now);
        Raise(new OrderAwaitingPayment(this, token, now));
        return Result.Success();
    }

    public Result Reject(string reason, DateTimeOffset now)
    {
        var check = RequireStatus(OrderStatus.Placed);
        if (check.IsFailure)
        {
            return check;
        }

        PaymentMethodToken = null;
        Transition(OrderStatus.Rejected, reason, now);
        Raise(new OrderRejected(this, reason, now));
        return Result.Success();
    }

    public Result MarkPaid(DateTimeOffset now)
    {
        var check = RequireStatus(OrderStatus.AwaitingPayment);
        if (check.IsFailure)
        {
            return check;
        }

        Transition(OrderStatus.Confirmed, null, now);
        Raise(new OrderConfirmed(this, now));
        return Result.Success();
    }

    public Result MarkPaymentFailed(string reason, DateTimeOffset now)
    {
        var check = RequireStatus(OrderStatus.AwaitingPayment);
        if (check.IsFailure)
        {
            return check;
        }

        Transition(OrderStatus.Cancelled, $"payment-failed: {reason}", now);
        Raise(new OrderCancelled(this, "payment-failed", StockWasReserved: true, CancelledBy: "service:payments", now));
        return Result.Success();
    }

    // Owners may cancel before payment (or after payment when the business allows it);
    // order managers may cancel any order that has not been fulfilled.
    public Result Cancel(string actor, bool isOwner, bool isStaff, bool ownerMayCancelAfterPayment, DateTimeOffset now)
    {
        var cancellable = Status switch
        {
            OrderStatus.Placed or OrderStatus.AwaitingPayment => isOwner || isStaff,
            OrderStatus.Confirmed => isStaff || (isOwner && ownerMayCancelAfterPayment),
            _ => false,
        };
        if (!cancellable)
        {
            return Status is OrderStatus.Placed or OrderStatus.AwaitingPayment or OrderStatus.Confirmed
                ? Error.Forbidden("orders.cancel.not-allowed", "You are not allowed to cancel this order at this stage.")
                : Error.Conflict("orders.cancel.invalid-state", $"An order that is {Status} cannot be cancelled.");
        }

        var stockWasReserved = Status == OrderStatus.AwaitingPayment;
        PaymentMethodToken = null;
        Transition(OrderStatus.Cancelled, "cancelled", now);
        Raise(new OrderCancelled(this, "cancelled", stockWasReserved, actor, now));
        return Result.Success();
    }

    public Result Fulfil(string actor, DateTimeOffset now)
    {
        var check = RequireStatus(OrderStatus.Confirmed);
        if (check.IsFailure)
        {
            return check;
        }

        Transition(OrderStatus.Fulfilled, null, now);
        Raise(new OrderFulfilled(this, actor, now));
        return Result.Success();
    }

    public Result MarkRefunded(DateTimeOffset now)
    {
        if (Status is not (OrderStatus.Confirmed or OrderStatus.Fulfilled or OrderStatus.Cancelled))
        {
            return Error.Conflict("orders.refund.invalid-state", $"An order that is {Status} cannot be refunded.");
        }

        Transition(OrderStatus.Refunded, "refunded", now);
        Raise(new OrderRefunded(this, now));
        return Result.Success();
    }

    private Result RequireStatus(OrderStatus expected) =>
        Status == expected
            ? Result.Success()
            : Error.Conflict("orders.state.invalid", $"Order is {Status}, expected {expected}.");

    private void Transition(OrderStatus status, string? reason, DateTimeOffset now)
    {
        Status = status;
        StatusReason = reason;
        UpdatedAt = now;
        Touch();
    }
}
