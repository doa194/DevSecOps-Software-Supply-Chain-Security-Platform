// Entry point of the Administration module: runtime feature flags.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Administration.Contracts;
using Commerce.Modules.Administration.Data;
using Commerce.Modules.Administration.Domain;
using Commerce.Modules.Administration.Features;
using Commerce.SharedKernel.Auditing;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;

namespace Commerce.Modules.Administration;

public sealed class AdministrationModule : IModule
{
    public string Name => AdministrationDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AdministrationDbContext>(configuration, AdministrationDbContext.SchemaName);
        services.AddOutboxPublisher<AdministrationDbContext>();
        services.AddSingleton<DatabaseFeatureDefinitionProvider>();
        services.AddSingleton<IFeatureDefinitionProvider>(sp => sp.GetRequiredService<DatabaseFeatureDefinitionProvider>());
        services.AddFeatureManagement();
        services.AddScoped<IDomainEventHandler<FeatureFlagChanged>, FeatureFlagChangedTranslation>();
        services.AddValidatorsFromAssemblyContaining<AdministrationModule>(includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        FeatureFlagEndpoints.Map(endpoints.MapGroup("/api/admin").WithTags("Administration"));

    private sealed class FeatureFlagChangedTranslation(Outbox<AdministrationDbContext> outbox, AuditTrail<AdministrationDbContext> audit) : IDomainEventHandler<FeatureFlagChanged>
    {
        public Task HandleAsync(FeatureFlagChanged e, CancellationToken cancellationToken)
        {
            outbox.Add(new FeatureFlagChangedV1(e.Name, e.Enabled, e.ChangedBy));
            audit.Record("administration.feature.toggle", "feature-flag", e.Name, AuditCategories.Security, actor: e.ChangedBy,
                details: new Dictionary<string, string> { ["enabled"] = e.Enabled.ToString() });
            return Task.CompletedTask;
        }
    }
}
