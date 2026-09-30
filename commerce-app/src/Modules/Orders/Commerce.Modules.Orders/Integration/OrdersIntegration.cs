// Orders' side of the order flow.
// Inbound: stock and payment outcomes move the order through its state machine.
// Outbound: domain events become integration events plus audit records.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Inventory.Contracts;
using Commerce.Modules.Orders.Contracts;
using Commerce.Modules.Orders.Data;
using Commerce.Modules.Orders.Domain;
using Commerce.Modules.Payments.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Orders.Integration;

internal abstract class OrderEventHandler<TEvent>(OrdersDbContext db, TimeProvider clock) : IIntegrationEventHandler<TEvent>
    where TEvent : SharedKernel.Messaging.IntegrationEvent
{
    protected TimeProvider Clock { get; } = clock;

    public async Task HandleAsync(TEvent integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == OrderId(integrationEvent), cancellationToken)
            ?? throw new InvalidOperationException($"Order {OrderId(integrationEvent)} does not exist.");
        // A transition that does not fit the current state means a duplicate or stale
        // message; the state machine refuses it and the order stays consistent.
        Apply(order, integrationEvent);
    }

    protected abstract Guid OrderId(TEvent integrationEvent);
    protected abstract void Apply(Order order, TEvent integrationEvent);
}

internal sealed class OnStockReserved(OrdersDbContext db, TimeProvider clock) : OrderEventHandler<StockReservedV1>(db, clock)
{
    protected override Guid OrderId(StockReservedV1 e) => e.OrderId;
    protected override void Apply(Order order, StockReservedV1 e) => order.MarkStockReserved(Clock.GetUtcNow());
}

internal sealed class OnStockReservationFailed(OrdersDbContext db, TimeProvider clock) : OrderEventHandler<StockReservationFailedV1>(db, clock)
{
    protected override Guid OrderId(StockReservationFailedV1 e) => e.OrderId;
    protected override void Apply(Order order, StockReservationFailedV1 e) => order.Reject($"out-of-stock: {string.Join(",", e.UnavailableSkus)}", Clock.GetUtcNow());
}

internal sealed class OnPaymentCaptured(OrdersDbContext db, TimeProvider clock) : OrderEventHandler<PaymentCapturedV1>(db, clock)
{
    protected override Guid OrderId(PaymentCapturedV1 e) => e.OrderId;
    protected override void Apply(Order order, PaymentCapturedV1 e) => order.MarkPaid(Clock.GetUtcNow());
}

internal sealed class OnPaymentFailed(OrdersDbContext db, TimeProvider clock) : OrderEventHandler<PaymentFailedV1>(db, clock)
{
    protected override Guid OrderId(PaymentFailedV1 e) => e.OrderId;
    protected override void Apply(Order order, PaymentFailedV1 e) => order.MarkPaymentFailed(e.Reason, Clock.GetUtcNow());
}

internal sealed class OnPaymentRefunded(OrdersDbContext db, TimeProvider clock) : OrderEventHandler<PaymentRefundedV1>(db, clock)
{
    protected override Guid OrderId(PaymentRefundedV1 e) => e.OrderId;

    protected override void Apply(Order order, PaymentRefundedV1 e)
    {
        if (e.FullyRefunded)
        {
            order.MarkRefunded(Clock.GetUtcNow());
        }
    }
}

internal sealed class OrderEventTranslation(Outbox<OrdersDbContext> outbox, AuditTrail<OrdersDbContext> audit) :
    IDomainEventHandler<OrderPlaced>,
    IDomainEventHandler<OrderAwaitingPayment>,
    IDomainEventHandler<OrderConfirmed>,
    IDomainEventHandler<OrderRejected>,
    IDomainEventHandler<OrderCancelled>,
    IDomainEventHandler<OrderFulfilled>,
    IDomainEventHandler<OrderRefunded>
{
    private static List<OrderLineV1> Lines(Order order) => order.Lines.Select(l => new OrderLineV1(l.Sku, l.Name, l.Quantity, l.UnitPrice)).ToList();

    public Task HandleAsync(OrderPlaced e, CancellationToken cancellationToken)
    {
        outbox.Add(new OrderPlacedV1(e.Order.Id, e.Order.CustomerId, Lines(e.Order), e.Order.Total, e.Order.Currency));
        audit.Record("orders.place", "order", e.Order.Id.ToString(), details: new Dictionary<string, string> { ["total"] = $"{e.Order.Total} {e.Order.Currency}" });
        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderAwaitingPayment e, CancellationToken cancellationToken)
    {
        outbox.Add(new OrderAwaitingPaymentV1
        {
            OrderId = e.Order.Id,
            CustomerId = e.Order.CustomerId,
            Amount = e.Order.Total,
            Currency = e.Order.Currency,
            PaymentMethodToken = e.PaymentMethodToken,
        });
        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderConfirmed e, CancellationToken cancellationToken)
    {
        outbox.Add(new OrderConfirmedV1(e.Order.Id, e.Order.CustomerId, Lines(e.Order), e.Order.Total, e.Order.Currency));
        OrderMetrics.Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "confirmed"));
        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderRejected e, CancellationToken cancellationToken)
    {
        outbox.Add(new OrderRejectedV1(e.Order.Id, e.Order.CustomerId, e.Reason));
        OrderMetrics.Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"));
        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderCancelled e, CancellationToken cancellationToken)
    {
        outbox.Add(new OrderCancelledV1(e.Order.Id, e.Order.CustomerId, e.Reason, e.StockWasReserved));
        audit.Record("orders.cancel", "order", e.Order.Id.ToString(), actor: e.CancelledBy, details: new Dictionary<string, string> { ["reason"] = e.Reason });
        OrderMetrics.Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "cancelled"));
        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderFulfilled e, CancellationToken cancellationToken)
    {
        outbox.Add(new OrderFulfilledV1(e.Order.Id, e.Order.CustomerId));
        audit.Record("orders.fulfil", "order", e.Order.Id.ToString(), actor: e.FulfilledBy);
        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderRefunded e, CancellationToken cancellationToken)
    {
        audit.Record("orders.refunded", "order", e.Order.Id.ToString(), actor: "service:payments");
        return Task.CompletedTask;
    }
}
