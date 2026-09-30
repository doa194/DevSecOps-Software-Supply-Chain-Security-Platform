// One PostgreSQL and one RabbitMQ container shared by all tests in the assembly; each test
// uses its own database or queue names so tests stay independent.
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

[assembly: AssemblyFixture(typeof(Commerce.IntegrationTests.Infrastructure.InfrastructureFixture))]

namespace Commerce.IntegrationTests.Infrastructure;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder(TestImages.Postgres).WithPassword("postgres").Build();
    public RabbitMqContainer RabbitMq { get; } = new RabbitMqBuilder(TestImages.RabbitMq).Build();

    public async ValueTask InitializeAsync() => await Task.WhenAll(Postgres.StartAsync(), RabbitMq.StartAsync());

    public async ValueTask DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await RabbitMq.DisposeAsync();
    }

    // A fresh database per test class, with the given roles pre-created (as PostgreSQL
    // roles with login and password equal to their name).
    public async Task<string> CreateDatabaseAsync(string name, params string[] roles)
    {
        await using var admin = new NpgsqlConnection(Postgres.GetConnectionString());
        await admin.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE); CREATE DATABASE {name};", admin))
        {
            await drop.ExecuteNonQueryAsync();
        }

        foreach (var role in roles)
        {
            await using var create = new NpgsqlCommand(
                $"DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') THEN CREATE ROLE {role} LOGIN PASSWORD '{role}'; END IF; END $$;" +
                $"GRANT CONNECT ON DATABASE {name} TO {role};", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public static string As(string connectionString, string role) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = role }.ConnectionString;
}
