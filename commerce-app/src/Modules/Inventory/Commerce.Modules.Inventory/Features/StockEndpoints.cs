// Staff endpoints for stock: look up a SKU, record goods received, list low stock.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Inventory.Data;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Inventory.Features;

public static class StockEndpoints
{
    public sealed record StockDto(string Sku, int OnHand, int Reserved, int Available, bool IsLow);

    public sealed record ReceiptRequest(int Quantity, string Reference);

    public sealed class ReceiptValidator : AbstractValidator<ReceiptRequest>
    {
        public ReceiptValidator()
        {
            RuleFor(r => r.Quantity).InclusiveBetween(1, 100_000);
            RuleFor(r => r.Reference).NotEmpty().MaximumLength(40).Matches("^[A-Za-z0-9-]+$");
        }
    }

    public static void Map(RouteGroupBuilder inventory)
    {
        inventory.MapGet("/{sku}", GetAsync).RequireAuthorization(Permissions.InventoryRead).WithName("GetStock");
        inventory.MapGet("/low-stock", LowStockAsync).RequireAuthorization(Permissions.InventoryRead).WithName("GetLowStock");
        inventory.MapPost("/{sku}/receipts", ReceiveAsync).RequireAuthorization(Permissions.InventoryWrite).Validate<ReceiptRequest>().WithName("ReceiveStock");
    }

    private static async Task<IResult> GetAsync(string sku, InventoryDbContext db, CancellationToken cancellationToken)
    {
        var item = await db.StockItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == sku, cancellationToken);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(new StockDto(item.Id, item.OnHand, item.Reserved, item.Available, item.IsLow));
    }

    private static async Task<IResult> LowStockAsync(InventoryDbContext db, CancellationToken cancellationToken)
    {
        // Small table; availability depends on reservations, so it is computed in memory.
        var items = await db.StockItems.AsNoTracking().ToListAsync(cancellationToken);
        var low = items.Where(i => i.IsLow).OrderBy(i => i.Available).Select(i => new StockDto(i.Id, i.OnHand, i.Reserved, i.Available, true)).ToList();
        return TypedResults.Ok(low);
    }

    private static async Task<IResult> ReceiveAsync(
        string sku, ReceiptRequest request, InventoryDbContext db, AuditTrail<InventoryDbContext> audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var item = await db.StockItems.FirstOrDefaultAsync(i => i.Id == sku, cancellationToken);
        if (item is null)
        {
            return TypedResults.NotFound();
        }

        var received = item.Receive(request.Quantity, clock.GetUtcNow());
        if (received.IsFailure)
        {
            return received.Error.ToProblem();
        }

        audit.Record("inventory.receipt", "stock-item", sku, details: new Dictionary<string, string>
        {
            ["quantity"] = request.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["reference"] = request.Reference,
        });
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new StockDto(item.Id, item.OnHand, item.Reserved, item.Available, item.IsLow));
    }
}
