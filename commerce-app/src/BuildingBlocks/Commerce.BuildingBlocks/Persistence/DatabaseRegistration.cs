// Registers a module's DbContext against its own connection string.
//
// Schema ownership is enforced by PostgreSQL, not only by convention: each module (and
// each worker) connects with its own database role, which has privileges on its own
// schema only. The Orders module physically cannot read the Customers tables.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Commerce.BuildingBlocks.Persistence;

public static class DatabaseRegistration
{
    public const string MigrationsHistoryTable = "__ef_migrations";

    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, IConfiguration configuration, string schema)
        where TContext : ModuleDbContext
    {
        var connectionString = configuration.GetConnectionString(schema)
            ?? throw new InvalidOperationException($"ConnectionStrings:{schema} is not configured.");

        services.AddDbContext<TContext>(options => Configure(options, connectionString, schema));
        // Scoped: handlers are scoped services that share the request's DbContext.
        services.TryAddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<Outbox<TContext>>();
        services.AddScoped<AuditTrail<TContext>>();
        services.AddHealthChecks().AddDbContextCheck<TContext>($"database-{schema}", tags: [HealthSetup.ReadyTag]);
        return services;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString, string schema) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable, schema))
            .UseSnakeCaseNamingConvention();
}

// Lets `dotnet ef migrations add` build a context without running the application. The
// connection string is never used to connect while generating migrations.
public abstract class DesignTimeFactory<TContext>(string schema) : IDesignTimeDbContextFactory<TContext>
    where TContext : ModuleDbContext
{
    public TContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        DatabaseRegistration.Configure(builder, "Host=localhost;Database=design_time", schema);
        var services = new ServiceCollection().BuildServiceProvider();
        return (TContext)Activator.CreateInstance(typeof(TContext), builder.Options, services)!;
    }
}
