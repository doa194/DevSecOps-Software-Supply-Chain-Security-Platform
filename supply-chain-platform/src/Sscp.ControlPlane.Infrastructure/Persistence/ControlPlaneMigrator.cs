// Applies migrations with the database owner role and grants the runtime role only the row
// access each table needs:
// - records with a lifecycle (builds, artifacts, releases, exceptions): read, insert, update;
// - history (evidence, findings, decisions, signatures, promotions, deployments, audit log):
//   read and insert only.
// The runtime role can never delete, so even a compromised Control Plane process cannot
// erase evidence or rewrite lifecycle history.
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sscp.ControlPlane.Infrastructure.Persistence;

public static partial class ControlPlaneMigrator
{
    public static async Task MigrateAsync(string ownerConnectionString, string runtimeRole, CancellationToken cancellationToken)
    {
        if (!SafeIdentifier().IsMatch(runtimeRole))
        {
            throw new InvalidOperationException($"'{runtimeRole}' is not an allowed role name.");
        }

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        InfrastructureRegistration.ConfigureDatabase(options, ownerConnectionString);
        await using var db = new ControlPlaneDbContext(options.Options);
        await db.Database.MigrateAsync(cancellationToken);

        // The role name is validated above; identifiers cannot be passed as parameters.
        const string schema = ControlPlaneDbContext.Schema;
        string[] lifecycle = ["builds", "artifacts", "releases", "risk_exceptions"];
        string[] history = ["evidence", "findings", "trust_decisions", "signatures", "promotions", "deployments", "audit_log"];
        var grants = new List<string>
        {
            $"GRANT USAGE ON SCHEMA {schema} TO {runtimeRole}",
            $"REVOKE ALL ON ALL TABLES IN SCHEMA {schema} FROM {runtimeRole}",
            $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {runtimeRole}",
        };
        grants.AddRange(lifecycle.Select(table => $"GRANT SELECT, INSERT, UPDATE ON {schema}.{table} TO {runtimeRole}"));
        grants.AddRange(history.Select(table => $"GRANT SELECT, INSERT ON {schema}.{table} TO {runtimeRole}"));

        // A table added by a future migration must be placed in one of the lists above;
        // failing here is better than silently granting it too much or nothing at all.
        var tables = await db.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = {schema} AND table_name <> '__ef_migrations'")
            .ToListAsync(cancellationToken);
        var unclassified = tables.Except(lifecycle).Except(history).ToList();
        if (unclassified.Count > 0)
        {
            throw new InvalidOperationException($"Tables without an access class: {string.Join(", ", unclassified)}.");
        }

        foreach (var grant in grants)
        {
            await db.Database.ExecuteSqlRawAsync(grant, cancellationToken); // nosemgrep: identifier validated above
        }
    }

    [GeneratedRegex("^[a-z][a-z_]{1,62}$")]
    private static partial Regex SafeIdentifier();
}

// Lets `dotnet ef migrations add` build the context without a running database.
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        InfrastructureRegistration.ConfigureDatabase(options, "Host=localhost;Database=design_time");
        return new ControlPlaneDbContext(options.Options);
    }
}
