// Entry point of the Inventory module: stock levels and order reservations.
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Catalog.Contracts;
using Commerce.Modules.Inventory.Data;
using Commerce.Modules.Inventory.Domain;
using Commerce.Modules.Inventory.Features;
using Commerce.Modules.Inventory.Integration;
using Commerce.Modules.Orders.Contracts;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Inventory;

public sealed class InventoryModule : IModule
{
    public string Name => InventoryDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<InventoryDbContext>(configuration, InventoryDbContext.SchemaName);
        services.AddOutboxPublisher<InventoryDbContext>();
        services.AddScoped<IDomainEventHandler<StockLevelChanged>, PublishStockLevels>();
        services.AddIntegrationEventConsumer("commerce-api.inventory", consumer => consumer
            .Handle<ProductCreatedV1, TrackNewProducts, InventoryDbContext>()
            .Handle<OrderPlacedV1, ReserveStockForOrder, InventoryDbContext>()
            .Handle<OrderCancelledV1, ReleaseStockForCancelledOrder, InventoryDbContext>()
            .Handle<OrderConfirmedV1, CommitStockForConfirmedOrder, InventoryDbContext>());
        services.AddValidatorsFromAssemblyContaining<InventoryModule>(includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        StockEndpoints.Map(endpoints.MapGroup("/api/inventory").WithTags("Inventory"));
}
