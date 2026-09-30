// A migrated Control Plane database in a disposable PostgreSQL container, with the runtime
// role created the way the platform bootstrap creates it. Migrations run twice to prove
// they (and the grants) can be re-applied on every start.
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sscp.ControlPlane.Infrastructure;
using Sscp.ControlPlane.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Sscp.ControlPlane.IntegrationTests;

public sealed class ControlPlaneDatabase : IAsyncLifetime
{
    public const string RuntimeRole = "controlplane_app";
    private const string RuntimePassword = "runtime-secret";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(
        "postgres:18.6-trixie@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722").Build();

    public string OwnerConnection => _postgres.GetConnectionString();

    public string RuntimeConnection => new NpgsqlConnectionStringBuilder(OwnerConnection) { Username = RuntimeRole, Password = RuntimePassword }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await ExecuteAsync(OwnerConnection, $"CREATE ROLE {RuntimeRole} LOGIN PASSWORD '{RuntimePassword}'");
        await ControlPlaneMigrator.MigrateAsync(OwnerConnection, RuntimeRole, CancellationToken.None);
        await ControlPlaneMigrator.MigrateAsync(OwnerConnection, RuntimeRole, CancellationToken.None);
    }

    public ControlPlaneDbContext RuntimeContext()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        InfrastructureRegistration.ConfigureDatabase(options, RuntimeConnection);
        return new ControlPlaneDbContext(options.Options);
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}
