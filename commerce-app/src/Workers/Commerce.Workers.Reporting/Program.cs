// Reporting/projection worker: builds the reporting read model from integration events
// and serves report queries (routed through the gateway at /api/reports).
using Commerce.BuildingBlocks.Health;
using Commerce.BuildingBlocks.Hosting;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Security;
using Commerce.Modules.Inventory.Contracts;
using Commerce.Modules.Orders.Contracts;
using Commerce.Modules.Payments.Contracts;
using Commerce.Workers.Reporting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerceServiceDefaults("reporting-worker");
builder.Services.AddCommerceMessaging(builder.Configuration,
    typeof(OrderPlacedV1).Assembly, typeof(PaymentRefundedV1).Assembly, typeof(StockLevelChangedV1).Assembly);
builder.Services.AddModuleDbContext<ReportingDbContext>(builder.Configuration, ReportingDbContext.SchemaName);
builder.Services.AddIntegrationEventConsumer("reporting-worker.projections", consumer => consumer
    .Handle<OrderPlacedV1, ProjectOrderPlaced, ReportingDbContext>()
    .Handle<OrderConfirmedV1, ProjectOrderConfirmed, ReportingDbContext>()
    .Handle<OrderRejectedV1, ProjectOrderRejected, ReportingDbContext>()
    .Handle<OrderCancelledV1, ProjectOrderCancelled, ReportingDbContext>()
    .Handle<OrderFulfilledV1, ProjectOrderFulfilled, ReportingDbContext>()
    .Handle<PaymentRefundedV1, ProjectRefund, ReportingDbContext>()
    .Handle<StockLevelChangedV1, ProjectStockLevel, ReportingDbContext>());
builder.Services.AddHealthChecks().AddRabbitMqReadiness();

var app = builder.Build();

if (args.Contains("migrate"))
{
    await MigrationRunner.MigrateAsync(app.Services, app.Configuration,
        [new(typeof(ReportingDbContext), ReportingDbContext.SchemaName, "commerce_reporting_worker")], app.Logger, CancellationToken.None);
    return;
}

app.UseCommerceServiceDefaults();

var reports = app.MapGroup("/api/reports").WithTags("Reports");

reports.MapGet("/sales/daily", async (DateOnly? from, DateOnly? to, ReportingDbContext db, CancellationToken cancellationToken) =>
{
    var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
    var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return TypedResults.Ok(await db.DailySales.AsNoTracking().Where(s => s.Day >= start && s.Day <= end).OrderBy(s => s.Day).ToListAsync(cancellationToken));
}).RequireAuthorization(Permissions.ReportsRead).WithName("DailySales");

reports.MapGet("/products/top", async (int? limit, ReportingDbContext db, CancellationToken cancellationToken) =>
    TypedResults.Ok(await db.ProductSales.AsNoTracking().OrderByDescending(p => p.Revenue).Take(Math.Clamp(limit ?? 10, 1, 100)).ToListAsync(cancellationToken)))
    .RequireAuthorization(Permissions.ReportsRead).WithName("TopProducts");

reports.MapGet("/orders/by-status", async (ReportingDbContext db, CancellationToken cancellationToken) =>
    TypedResults.Ok(await db.OrderSummaries.AsNoTracking().GroupBy(s => s.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken)))
    .RequireAuthorization(Permissions.ReportsRead).WithName("OrdersByStatus");

reports.MapGet("/inventory/low-stock", async (int? threshold, ReportingDbContext db, CancellationToken cancellationToken) =>
    TypedResults.Ok(await db.StockLevels.AsNoTracking().Where(s => s.Available <= (threshold ?? 5)).OrderBy(s => s.Available).ToListAsync(cancellationToken)))
    .RequireAuthorization(Permissions.InventoryRead).WithName("LowStockReport");

await app.RunAsync();
