// Entry point of the Customers module: customer profiles and addresses (personal data).
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Customers.Contracts;
using Commerce.Modules.Customers.Data;
using Commerce.Modules.Customers.Domain;
using Commerce.Modules.Customers.Features;
using Commerce.Modules.Customers.Integration;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Customers;

public sealed class CustomersModule : IModule
{
    public string Name => CustomersDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CustomersDbContext>(configuration, CustomersDbContext.SchemaName);
        services.AddOutboxPublisher<CustomersDbContext>();
        services.AddScoped<ICustomerDirectory, CustomerDirectory>();
        services.AddScoped<IDomainEventHandler<CustomerRegistered>, CustomerEventTranslation>();
        services.AddValidatorsFromAssemblyContaining<CustomersModule>(includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var customers = endpoints.MapGroup("/api/customers").WithTags("Customers");
        MyProfile.Map(customers);
        StaffCustomerLookup.Map(customers);
    }
}
