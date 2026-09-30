// Entry point of the Orders module: order placement, the order lifecycle and order queries.
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Inventory.Contracts;
using Commerce.Modules.Orders.Data;
using Commerce.Modules.Orders.Domain;
using Commerce.Modules.Orders.Features;
using Commerce.Modules.Orders.Integration;
using Commerce.Modules.Payments.Contracts;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Orders;

public sealed class OrdersModule : IModule
{
    public string Name => OrdersDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<OrdersDbContext>(configuration, OrdersDbContext.SchemaName);
        services.AddOutboxPublisher<OrdersDbContext>();
        services.AddScoped<OrderEventTranslation>();
        services.AddScoped<IDomainEventHandler<OrderPlaced>>(sp => sp.GetRequiredService<OrderEventTranslation>());
        services.AddScoped<IDomainEventHandler<OrderAwaitingPayment>>(sp => sp.GetRequiredService<OrderEventTranslation>());
        services.AddScoped<IDomainEventHandler<OrderConfirmed>>(sp => sp.GetRequiredService<OrderEventTranslation>());
        services.AddScoped<IDomainEventHandler<OrderRejected>>(sp => sp.GetRequiredService<OrderEventTranslation>());
        services.AddScoped<IDomainEventHandler<OrderCancelled>>(sp => sp.GetRequiredService<OrderEventTranslation>());
        services.AddScoped<IDomainEventHandler<OrderFulfilled>>(sp => sp.GetRequiredService<OrderEventTranslation>());
        services.AddScoped<IDomainEventHandler<OrderRefunded>>(sp => sp.GetRequiredService<OrderEventTranslation>());
        services.AddIntegrationEventConsumer("commerce-api.orders", consumer => consumer
            .Handle<StockReservedV1, OnStockReserved, OrdersDbContext>()
            .Handle<StockReservationFailedV1, OnStockReservationFailed, OrdersDbContext>()
            .Handle<PaymentCapturedV1, OnPaymentCaptured, OrdersDbContext>()
            .Handle<PaymentFailedV1, OnPaymentFailed, OrdersDbContext>()
            .Handle<PaymentRefundedV1, OnPaymentRefunded, OrdersDbContext>());
        services.AddValidatorsFromAssemblyContaining<OrdersModule>(includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/orders").WithTags("Orders");
        PlaceOrder.Map(orders);
        OrderQueries.Map(orders);
        ManageOrder.Map(orders);
    }
}
