// Entry point of the Catalog module: products, prices and the public storefront listing.
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Catalog.Contracts;
using Commerce.Modules.Catalog.Data;
using Commerce.Modules.Catalog.Domain;
using Commerce.Modules.Catalog.Features;
using Commerce.Modules.Catalog.Integration;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Catalog;

public sealed class CatalogModule : IModule
{
    public string Name => CatalogDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CatalogDbContext>(configuration, CatalogDbContext.SchemaName);
        services.AddOutboxPublisher<CatalogDbContext>();
        services.AddScoped<ProductListCache>();
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<IDomainEventHandler<ProductCreated>, CatalogEventTranslation>();
        services.AddScoped<IDomainEventHandler<ProductPriceChanged>, CatalogEventTranslation>();
        services.AddScoped<IDomainEventHandler<ProductRetired>, CatalogEventTranslation>();
        services.AddValidatorsFromAssemblyContaining<CatalogModule>(includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.MapGroup("/api/catalog").WithTags("Catalog");
        BrowseProducts.Map(catalog);
        GetProduct.Map(catalog);
        CreateProduct.Map(catalog);
        ManageProduct.Map(catalog);
    }
}
