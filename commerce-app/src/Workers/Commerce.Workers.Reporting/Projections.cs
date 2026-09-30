// Projection handlers: each applies one integration event to the reporting tables.
using Commerce.BuildingBlocks.Messaging;
using Commerce.Modules.Inventory.Contracts;
using Commerce.Modules.Orders.Contracts;
using Commerce.Modules.Payments.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Workers.Reporting;

internal static class ProjectionHelpers
{
    public static async Task<DailySales> DayAsync(ReportingDbContext db, DateTimeOffset at, string currency, CancellationToken cancellationToken)
    {
        var day = DateOnly.FromDateTime(at.UtcDateTime);
        var row = await db.DailySales.FirstOrDefaultAsync(s => s.Day == day && s.Currency == currency, cancellationToken);
        if (row is null)
        {
            row = new DailySales { Day = day, Currency = currency };
            db.DailySales.Add(row);
        }

        return row;
    }

    // Applies a status change only when the event is newer than what the summary shows, so
    // late or re-delivered events cannot move an order "backwards" in the report.
    public static async Task UpdateStatusAsync(ReportingDbContext db, Guid orderId, string status, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var summary = await db.OrderSummaries.FirstOrDefaultAsync(s => s.OrderId == orderId, cancellationToken);
        if (summary is null)
        {
            throw new InvalidOperationException($"Order {orderId} is not projected yet; retrying later.");
        }

        if (occurredAt >= summary.LastEventAt)
        {
            summary.Status = status;
            summary.LastEventAt = occurredAt;
        }
    }
}

internal sealed class ProjectOrderPlaced(ReportingDbContext db) : IIntegrationEventHandler<OrderPlacedV1>
{
    public async Task HandleAsync(OrderPlacedV1 e, MessageContext context, CancellationToken cancellationToken)
    {
        if (!await db.OrderSummaries.AnyAsync(s => s.OrderId == e.OrderId, cancellationToken))
        {
            db.OrderSummaries.Add(new OrderSummary
            {
                OrderId = e.OrderId, CustomerId = e.CustomerId, Status = "Placed", Total = e.Total, Currency = e.Currency,
                PlacedAt = e.OccurredAt, LastEventAt = e.OccurredAt,
            });
        }
    }
}

internal sealed class ProjectOrderConfirmed(ReportingDbContext db) : IIntegrationEventHandler<OrderConfirmedV1>
{
    public async Task HandleAsync(OrderConfirmedV1 e, MessageContext context, CancellationToken cancellationToken)
    {
        await ProjectionHelpers.UpdateStatusAsync(db, e.OrderId, "Confirmed", e.OccurredAt, cancellationToken);
        var day = await ProjectionHelpers.DayAsync(db, e.OccurredAt, e.Currency, cancellationToken);
        day.OrdersConfirmed++;
        day.Revenue += e.Total;

        foreach (var line in e.Lines)
        {
            var product = await db.ProductSales.FirstOrDefaultAsync(p => p.Sku == line.Sku, cancellationToken)
                ?? db.ProductSales.Add(new ProductSales { Sku = line.Sku }).Entity;
            product.Name = line.Name;
            product.UnitsSold += line.Quantity;
            product.Revenue += line.UnitPrice * line.Quantity;
        }
    }
}

internal sealed class ProjectOrderRejected(ReportingDbContext db) : IIntegrationEventHandler<OrderRejectedV1>
{
    public async Task HandleAsync(OrderRejectedV1 e, MessageContext context, CancellationToken cancellationToken)
    {
        await ProjectionHelpers.UpdateStatusAsync(db, e.OrderId, "Rejected", e.OccurredAt, cancellationToken);
        var summary = await db.OrderSummaries.FirstAsync(s => s.OrderId == e.OrderId, cancellationToken);
        (await ProjectionHelpers.DayAsync(db, e.OccurredAt, summary.Currency, cancellationToken)).OrdersRejected++;
    }
}

internal sealed class ProjectOrderCancelled(ReportingDbContext db) : IIntegrationEventHandler<OrderCancelledV1>
{
    public async Task HandleAsync(OrderCancelledV1 e, MessageContext context, CancellationToken cancellationToken)
    {
        await ProjectionHelpers.UpdateStatusAsync(db, e.OrderId, "Cancelled", e.OccurredAt, cancellationToken);
        var summary = await db.OrderSummaries.FirstAsync(s => s.OrderId == e.OrderId, cancellationToken);
        (await ProjectionHelpers.DayAsync(db, e.OccurredAt, summary.Currency, cancellationToken)).OrdersCancelled++;
    }
}

internal sealed class ProjectOrderFulfilled(ReportingDbContext db) : IIntegrationEventHandler<OrderFulfilledV1>
{
    public Task HandleAsync(OrderFulfilledV1 e, MessageContext context, CancellationToken cancellationToken) =>
        ProjectionHelpers.UpdateStatusAsync(db, e.OrderId, "Fulfilled", e.OccurredAt, cancellationToken);
}

internal sealed class ProjectRefund(ReportingDbContext db) : IIntegrationEventHandler<PaymentRefundedV1>
{
    public async Task HandleAsync(PaymentRefundedV1 e, MessageContext context, CancellationToken cancellationToken)
    {
        (await ProjectionHelpers.DayAsync(db, e.OccurredAt, e.Currency, cancellationToken)).Refunds += e.Amount;
        if (e.FullyRefunded)
        {
            await ProjectionHelpers.UpdateStatusAsync(db, e.OrderId, "Refunded", e.OccurredAt, cancellationToken);
        }
    }
}

internal sealed class ProjectStockLevel(ReportingDbContext db) : IIntegrationEventHandler<StockLevelChangedV1>
{
    public async Task HandleAsync(StockLevelChangedV1 e, MessageContext context, CancellationToken cancellationToken)
    {
        var level = await db.StockLevels.FirstOrDefaultAsync(s => s.Sku == e.Sku, cancellationToken)
            ?? db.StockLevels.Add(new StockLevel { Sku = e.Sku }).Entity;
        if (e.OccurredAt >= level.UpdatedAt)
        {
            (level.OnHand, level.Reserved, level.Available, level.UpdatedAt) = (e.OnHand, e.Reserved, e.Available, e.OccurredAt);
        }
    }
}
