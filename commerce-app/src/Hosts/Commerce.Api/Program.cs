// Host process of the modular monolith. It composes the eight business modules; each
// module registers its own services, database schema, consumers and endpoints through the
// IModule contract.
//
// `Commerce.Api migrate` applies every module's migrations with the schema-owner
// credential and exits. The long-running API never receives that credential.
using Commerce.BuildingBlocks.Health;
using Commerce.BuildingBlocks.Hosting;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Storage;
using Commerce.Modules.Administration;
using Commerce.Modules.Administration.Data;
using Commerce.Modules.Catalog;
using Commerce.Modules.Catalog.Data;
using Commerce.Modules.Customers;
using Commerce.Modules.Customers.Data;
using Commerce.Modules.Documents;
using Commerce.Modules.Documents.Data;
using Commerce.Modules.Identity;
using Commerce.Modules.Identity.Data;
using Commerce.Modules.Inventory;
using Commerce.Modules.Inventory.Data;
using Commerce.Modules.Orders;
using Commerce.Modules.Orders.Data;
using Commerce.Modules.Payments;
using Commerce.Modules.Payments.Data;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerceServiceDefaults("commerce-api");

IModule[] modules =
[
    new IdentityModule(), new CustomersModule(), new CatalogModule(), new InventoryModule(),
    new OrdersModule(), new PaymentsModule(), new DocumentsModule(), new AdministrationModule(),
];

builder.Services.AddCommerceMessaging(builder.Configuration,
    typeof(Commerce.Modules.Identity.Contracts.UserProvisionedV1).Assembly,
    typeof(Commerce.Modules.Customers.Contracts.CustomerRegisteredV1).Assembly,
    typeof(Commerce.Modules.Catalog.Contracts.ProductCreatedV1).Assembly,
    typeof(Commerce.Modules.Inventory.Contracts.StockReservedV1).Assembly,
    typeof(Commerce.Modules.Orders.Contracts.OrderPlacedV1).Assembly,
    typeof(Commerce.Modules.Payments.Contracts.PaymentCapturedV1).Assembly,
    typeof(Commerce.Modules.Documents.Contracts.DocumentGeneratedV1).Assembly,
    typeof(Commerce.Modules.Administration.Contracts.FeatureFlagChangedV1).Assembly);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("redis") ?? throw new InvalidOperationException("ConnectionStrings:redis is required.")));
builder.Services.AddObjectStorage(builder.Configuration);
builder.Services.AddHealthChecks().AddRabbitMqReadiness().AddRedisReadiness();
builder.Services.AddOpenApi();

foreach (var module in modules)
{
    module.Register(builder.Services, builder.Configuration);
}

var app = builder.Build();

if (args.Contains("migrate"))
{
    await MigrationRunner.MigrateAsync(app.Services, app.Configuration,
    [
        new(typeof(IdentityDbContext), IdentityDbContext.SchemaName, "commerce_identity"),
        new(typeof(CustomersDbContext), CustomersDbContext.SchemaName, "commerce_customers"),
        new(typeof(CatalogDbContext), CatalogDbContext.SchemaName, "commerce_catalog"),
        new(typeof(InventoryDbContext), InventoryDbContext.SchemaName, "commerce_inventory"),
        new(typeof(OrdersDbContext), OrdersDbContext.SchemaName, "commerce_orders"),
        new(typeof(PaymentsDbContext), PaymentsDbContext.SchemaName, "commerce_payments"),
        new(typeof(DocumentsDbContext), DocumentsDbContext.SchemaName, "commerce_documents"),
        new(typeof(AdministrationDbContext), AdministrationDbContext.SchemaName, "commerce_administration"),
    ], app.Logger, CancellationToken.None);
    await app.Services.GetRequiredService<IObjectStorage>().EnsureBucketAsync(CancellationToken.None);
    return;
}

app.UseCommerceServiceDefaults();

// The OpenAPI document is published only where it is useful (development and the
// security-test environment, where ZAP reads it); it is off in the cluster.
if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi().AllowAnonymous();
}

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

await app.RunAsync();

public partial class Program;
