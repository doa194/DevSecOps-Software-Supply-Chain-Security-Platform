// Entry point of the Identity module: integration with Keycloak (account provisioning and
// role administration). Authentication itself is handled by the shared JWT setup.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Modules;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Identity.Contracts;
using Commerce.Modules.Identity.Data;
using Commerce.Modules.Identity.Domain;
using Commerce.Modules.Identity.Features;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Modules.Identity;

public sealed class IdentityModule : IModule
{
    public string Name => IdentityDbContext.SchemaName;

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<IdentityDbContext>(configuration, IdentityDbContext.SchemaName);
        services.AddOutboxPublisher<IdentityDbContext>();
        services.AddScoped<IDomainEventHandler<UserProvisioned>, UserProvisionedTranslation>();
        KeycloakAdminClient.Register(services, configuration);
        services.AddValidatorsFromAssemblyContaining<IdentityModule>(includeInternalTypes: true);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var identity = endpoints.MapGroup("/api/identity").WithTags("Identity");
        CurrentAccount.Map(identity);
        ChangeUserRoles.Map(identity);
    }

    private sealed class UserProvisionedTranslation(Outbox<IdentityDbContext> outbox, AuditTrail<IdentityDbContext> audit) : IDomainEventHandler<UserProvisioned>
    {
        public Task HandleAsync(UserProvisioned domainEvent, CancellationToken cancellationToken)
        {
            outbox.Add(new UserProvisionedV1(domainEvent.Subject, domainEvent.Username));
            audit.Record("identity.account.provision", "user", domainEvent.Subject, actor: domainEvent.Subject);
            return Task.CompletedTask;
        }
    }
}
