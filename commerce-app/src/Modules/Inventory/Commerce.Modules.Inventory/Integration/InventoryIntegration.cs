// Inventory's part of the order flow, driven by integration events:
//   ProductCreated  -> start tracking the SKU
//   OrderPlaced     -> reserve all lines, answer StockReserved or StockReservationFailed
//   OrderCancelled  -> release the reservation
//   OrderConfirmed  -> commit the reservation (stock leaves the warehouse)
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Catalog.Contracts;
using Commerce.Modules.Inventory.Contracts;
using Commerce.Modules.Inventory.Data;
using Commerce.Modules.Inventory.Domain;
using Commerce.Modules.Orders.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Inventory.Integration;

internal sealed class TrackNewProducts(InventoryDbContext db) : IIntegrationEventHandler<ProductCreatedV1>
{
    public async Task HandleAsync(ProductCreatedV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        if (!await db.StockItems.AnyAsync(i => i.Id == integrationEvent.Sku, cancellationToken))
        {
            db.StockItems.Add(StockItem.Track(integrationEvent.Sku));
        }
    }
}

internal sealed class ReserveStockForOrder(InventoryDbContext db, Outbox<InventoryDbContext> outbox, TimeProvider clock) : IIntegrationEventHandler<OrderPlacedV1>
{
    public async Task HandleAsync(OrderPlacedV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        var skus = integrationEvent.Lines.Select(l => l.Sku).Distinct().ToList();
        var stock = await db.StockItems.Where(i => skus.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var lines = integrationEvent.Lines.Select(l => new ReservationService.Line(l.Sku, l.Quantity)).ToList();

        var reserved = ReservationService.Reserve(integrationEvent.OrderId, lines, stock, clock.GetUtcNow());
        outbox.Add(reserved.IsSuccess
            ? new StockReservedV1(integrationEvent.OrderId)
            : new StockReservationFailedV1(integrationEvent.OrderId, reserved.Error.Message.Split(',')));
    }
}

internal sealed class ReleaseStockForCancelledOrder(InventoryDbContext db, TimeProvider clock) : IIntegrationEventHandler<OrderCancelledV1>
{
    public async Task HandleAsync(OrderCancelledV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        var items = await db.StockItems.Where(i => i.Reservations.Any(r => r.OrderId == integrationEvent.OrderId)).ToListAsync(cancellationToken);
        items.ForEach(item => item.Release(integrationEvent.OrderId, clock.GetUtcNow()));
    }
}

internal sealed class CommitStockForConfirmedOrder(InventoryDbContext db, TimeProvider clock) : IIntegrationEventHandler<OrderConfirmedV1>
{
    public async Task HandleAsync(OrderConfirmedV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        var items = await db.StockItems.Where(i => i.Reservations.Any(r => r.OrderId == integrationEvent.OrderId)).ToListAsync(cancellationToken);
        items.ForEach(item => item.Commit(integrationEvent.OrderId, clock.GetUtcNow()));
    }
}

internal sealed class PublishStockLevels(Outbox<InventoryDbContext> outbox) : IDomainEventHandler<StockLevelChanged>
{
    public Task HandleAsync(StockLevelChanged domainEvent, CancellationToken cancellationToken)
    {
        outbox.Add(new StockLevelChangedV1(domainEvent.Sku, domainEvent.OnHand, domainEvent.Reserved, domainEvent.Available));
        return Task.CompletedTask;
    }
}
