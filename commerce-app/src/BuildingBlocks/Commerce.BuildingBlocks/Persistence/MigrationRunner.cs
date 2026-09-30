// Applies EF Core migrations and grants runtime privileges, run as a separate step
// (`<service> migrate`, a Kubernetes init container) with the schema-owner credential.
//
// Runtime processes never hold the owner credential: they connect with a role that can
// read and write rows in its own schema but cannot create, alter or drop anything. For
// append-only schemas (the audit trail) the runtime role cannot even update or delete.
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Commerce.BuildingBlocks.Persistence;

public sealed record MigrationTarget(Type ContextType, string Schema, string RuntimeRole, bool AppendOnly = false);

public static partial class MigrationRunner
{
    public const string OwnerConnectionName = "migrations";

    public static async Task MigrateAsync(IServiceProvider services, IConfiguration configuration, IEnumerable<MigrationTarget> targets, ILogger logger, CancellationToken cancellationToken)
    {
        var ownerConnection = configuration.GetConnectionString(OwnerConnectionName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{OwnerConnectionName} is required to run migrations.");

        foreach (var target in targets)
        {
            // Validate before touching the database: a bad name fails the whole run.
            var schema = SqlIdentifier.Parse(target.Schema);
            var role = SqlIdentifier.Parse(target.RuntimeRole);

            var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(target.ContextType))!;
            DatabaseRegistration.Configure(builder, ownerConnection, target.Schema);
            await using var context = (ModuleDbContext)ActivatorUtilities.CreateInstance(services, target.ContextType, builder.Options);

            await context.Database.MigrateAsync(cancellationToken);
            foreach (var statement in PrivilegeStatements.RuntimeGrants(schema, role, SqlIdentifier.Parse(DatabaseRegistration.MigrationsHistoryTable), target.AppendOnly))
            {
                await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
            }

            LogMigrated(logger, target.Schema, target.RuntimeRole);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated schema {Schema}; runtime role {Role} granted row access")]
    private static partial void LogMigrated(ILogger logger, string schema, string role);
}
