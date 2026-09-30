// Public contract of the Orders module. The order lifecycle is coordinated through these
// events: Inventory reserves stock for OrderPlaced, Payments captures OrderAwaitingPayment,
// Documents, Notifications and Reporting react to the outcome.
using Commerce.SharedKernel.Classification;
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Orders.Contracts;

public sealed record OrderLineV1(string Sku, string Name, int Quantity, decimal UnitPrice);

[IntegrationEvent("orders.order-placed", 1)]
public sealed record OrderPlacedV1(Guid OrderId, Guid CustomerId, IReadOnlyList<OrderLineV1> Lines, decimal Total, string Currency) : IntegrationEvent;

[IntegrationEvent("orders.order-awaiting-payment", 1)]
public sealed record OrderAwaitingPaymentV1 : IntegrationEvent
{
    public required Guid OrderId { get; init; }
    public required Guid CustomerId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }

    // Opaque token from the (simulated) payment provider, never a card number.
    [FinancialData]
    public required string PaymentMethodToken { get; init; }
}

[IntegrationEvent("orders.order-confirmed", 1)]
public sealed record OrderConfirmedV1(Guid OrderId, Guid CustomerId, IReadOnlyList<OrderLineV1> Lines, decimal Total, string Currency) : IntegrationEvent;

[IntegrationEvent("orders.order-rejected", 1)]
public sealed record OrderRejectedV1(Guid OrderId, Guid CustomerId, string Reason) : IntegrationEvent;

[IntegrationEvent("orders.order-cancelled", 1)]
public sealed record OrderCancelledV1(Guid OrderId, Guid CustomerId, string Reason, bool StockWasReserved) : IntegrationEvent;

[IntegrationEvent("orders.order-fulfilled", 1)]
public sealed record OrderFulfilledV1(Guid OrderId, Guid CustomerId) : IntegrationEvent;
