// Contract between the API host and each business module. The host knows only this
// interface: every module registers its own services and endpoints, so adding or
// removing a module never requires touching another module.
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.BuildingBlocks.Modules;

public interface IModule
{
    // Short lower-case name, also used as the module's database schema and route prefix.
    string Name { get; }

    void Register(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
