// Schema ownership is enforced by PostgreSQL privileges, not just by convention. These
// tests run the real migrations and grants once, then connect as the runtime roles.
using Commerce.BuildingBlocks.Persistence;
using Commerce.IntegrationTests.Infrastructure;
using Commerce.Modules.Customers.Data;
using Commerce.Modules.Orders.Data;
using Commerce.Workers.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Commerce.IntegrationTests.Persistence;

public sealed class MigratedDatabaseFixture(InfrastructureFixture infrastructure) : IAsyncLifetime
{
    public string Database { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        Database = await infrastructure.CreateDatabaseAsync("ownership", "commerce_orders", "commerce_customers", "commerce_audit_worker");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:migrations"] = Database }).Build();
        await MigrationRunner.MigrateAsync(new ServiceCollection().BuildServiceProvider(), configuration,
        [
            new(typeof(OrdersDbContext), OrdersDbContext.SchemaName, "commerce_orders"),
            new(typeof(CustomersDbContext), CustomersDbContext.SchemaName, "commerce_customers"),
            new(typeof(AuditDbContext), AuditDbContext.SchemaName, "commerce_audit_worker", AppendOnly: true),
        ], NullLogger.Instance, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SchemaOwnershipTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    [Fact]
    public async Task A_module_role_can_use_its_own_schema() =>
        Assert.Null(await RunAsync("commerce_orders", "SELECT count(*) FROM orders.orders"));

    [Fact]
    public async Task A_module_role_cannot_read_another_modules_schema() =>
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await RunAsync("commerce_orders", "SELECT count(*) FROM customers.customers"));

    [Fact]
    public async Task A_runtime_role_cannot_change_the_schema() =>
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await RunAsync("commerce_orders", "CREATE TABLE orders.shadow (id int)"));

    [Theory]
    [InlineData("UPDATE audit.audit_log SET actor = 'someone-else'")]
    [InlineData("DELETE FROM audit.audit_log")]
    [InlineData("TRUNCATE audit.audit_log")]
    public async Task The_audit_worker_cannot_rewrite_history(string statement) =>
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, await RunAsync("commerce_audit_worker", statement));

    [Fact]
    public async Task The_audit_worker_can_append() =>
        Assert.Null(await RunAsync("commerce_audit_worker",
            "INSERT INTO audit.audit_log (event_id, occurred_at, recorded_at, source, actor, action, category, resource_type, resource_id, outcome, details, previous_hash, hash) " +
            "VALUES (gen_random_uuid(), now(), now(), 's', 'a', 'x', 'business', 'r', '1', 'succeeded', '{}', 'p', 'h')"));

    private async Task<string?> RunAsync(string role, string sql)
    {
        await using var connection = new NpgsqlConnection(InfrastructureFixture.As(database.Database, role));
        await connection.OpenAsync();
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException error)
        {
            return error.SqlState;
        }
    }
}
