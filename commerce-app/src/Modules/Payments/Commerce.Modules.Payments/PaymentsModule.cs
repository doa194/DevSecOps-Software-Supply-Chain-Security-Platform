// Entry point of the Payments module: captures, failures and refunds.
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Orders.Contracts;
using Commerce.Modules.Payments.Data;
using Commerce.Modules.Payments.Domain;
using Commerce.Modules.Payments.Features;
using Commerce.Modules.Payments.Integration;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Payments;

public sealed class PaymentsModule : IModule
{
    public string Name => PaymentsDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<PaymentsDbContext>(configuration, PaymentsDbContext.SchemaName);
        services.AddOutboxPublisher<PaymentsDbContext>();
        services.AddSingleton<IPaymentProvider, CardProviderSimulator>();
        services.AddSingleton<IPaymentProvider, WalletProviderSimulator>();
        services.AddSingleton<PaymentProviderResolver>();
        services.AddScoped<PaymentEventTranslation>();
        services.AddScoped<IDomainEventHandler<PaymentCaptured>>(sp => sp.GetRequiredService<PaymentEventTranslation>());
        services.AddScoped<IDomainEventHandler<PaymentFailed>>(sp => sp.GetRequiredService<PaymentEventTranslation>());
        services.AddScoped<IDomainEventHandler<PaymentRefunded>>(sp => sp.GetRequiredService<PaymentEventTranslation>());
        services.AddIntegrationEventConsumer("commerce-api.payments", consumer => consumer
            .Handle<OrderAwaitingPaymentV1, CapturePaymentForOrder, PaymentsDbContext>());
        services.AddValidatorsFromAssemblyContaining<PaymentsModule>(includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        PaymentEndpoints.Map(endpoints.MapGroup("/api/payments").WithTags("Payments"));
}
