// Runs the real Commerce.Api host against disposable PostgreSQL, Redis and RabbitMQ.
// Only two things are replaced: token validation trusts the test signing key, and the
// Keycloak admin client is a recorder (Keycloak itself is covered by integration tests).
using System.Security.Cryptography;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Administration.Data;
using Commerce.Modules.Catalog.Data;
using Commerce.Modules.Customers.Data;
using Commerce.Modules.Documents.Data;
using Commerce.Modules.Identity.Data;
using Commerce.Modules.Inventory.Data;
using Commerce.Modules.Orders.Data;
using Commerce.Modules.Payments.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Commerce.ComponentTests.Infrastructure.CommerceApiFactory))]

namespace Commerce.ComponentTests.Infrastructure;

public sealed class CommerceApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly (Type Context, string Schema)[] Modules =
    [
        (typeof(IdentityDbContext), IdentityDbContext.SchemaName), (typeof(CustomersDbContext), CustomersDbContext.SchemaName),
        (typeof(CatalogDbContext), CatalogDbContext.SchemaName), (typeof(InventoryDbContext), InventoryDbContext.SchemaName),
        (typeof(OrdersDbContext), OrdersDbContext.SchemaName), (typeof(PaymentsDbContext), PaymentsDbContext.SchemaName),
        (typeof(DocumentsDbContext), DocumentsDbContext.SchemaName), (typeof(AdministrationDbContext), AdministrationDbContext.SchemaName),
    ];

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6-trixie@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8.6.7-alpine@sha256:ac2da09bc822f325a9f68f533120b86fa39c1b8db46b3aa7c1bbea7fdfd4fcba").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.2.9-management-alpine@sha256:cb84d1cee317570eeb9b61d7c64df519399ecb450d16ec93aa035c1d42501475").Build();
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public RecordingKeycloakAdmin Keycloak { get; } = new();
    public string DatabaseConnection => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync());

        // Runtime roles must exist for the grants; the API itself connects as the owner here
        // (role separation is verified by the integration tests).
        await using (var admin = new NpgsqlConnection(DatabaseConnection))
        {
            await admin.OpenAsync();
            foreach (var (_, schema) in Modules)
            {
                await using var create = new NpgsqlCommand($"CREATE ROLE commerce_{schema} NOLOGIN", admin);
                await create.ExecuteNonQueryAsync();
            }
        }

        var migrationConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:migrations"] = DatabaseConnection }).Build();
        await MigrationRunner.MigrateAsync(new ServiceCollection().BuildServiceProvider(), migrationConfig,
            Modules.Select(m => new MigrationTarget(m.Context, m.Schema, $"commerce_{m.Schema}")), NullLogger.Instance, CancellationToken.None);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Identity:Issuer"] = TestTokens.Issuer,
            ["ConnectionStrings:redis"] = _redis.GetConnectionString(),
            ["Messaging:ConnectionString"] = _rabbit.GetConnectionString(),
            ["Messaging:Source"] = "commerce-api",
            ["Messaging:Signing:KeyId"] = "api-key",
            ["Messaging:Signing:PrivateKeyPem"] = _signingKey.ExportPkcs8PrivateKeyPem(),
            ["ObjectStorage:Endpoint"] = "http://127.0.0.1:9",
        };
        foreach (var (_, schema) in Modules)
        {
            settings[$"ConnectionStrings:{schema}"] = DatabaseConnection;
        }

        // UseSetting (not ConfigureAppConfiguration): Program.cs reads configuration while it
        // registers modules, before app-configuration callbacks would be applied.
        settings["OpenApi:Enabled"] = "false";
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                configuration.SigningKeys.Add(TestTokens.SigningKey);
                options.Configuration = configuration;
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
            });
            services.AddSingleton<Commerce.Modules.Identity.Data.IKeycloakAdmin>(Keycloak);
        });
    }

    public HttpClient ClientFor(string? username, params string[] roles)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (username is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestTokens.For(username, roles));
        }

        return client;
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
        _signingKey.Dispose();
    }
}

// Stands in for Keycloak's admin API and records what the application asked it to do.
public sealed class RecordingKeycloakAdmin : Commerce.Modules.Identity.Data.IKeycloakAdmin
{
    // Keycloak answers 404 for this user id.
    public const string UnknownUser = "0f0f0f0f-0000-4000-8000-00000000dead";

    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<string>?> GetRealmRolesAsync(string userId, CancellationToken cancellationToken)
    {
        Calls.Add($"get:{userId}");
        return Task.FromResult<IReadOnlyList<string>?>(userId == UnknownUser ? null : ["customer"]);
    }

    public Task AddRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        Calls.Add($"add:{userId}:{string.Join(",", roles)}");
        return Task.CompletedTask;
    }

    public Task RemoveRealmRolesAsync(string userId, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        Calls.Add($"remove:{userId}:{string.Join(",", roles)}");
        return Task.CompletedTask;
    }
}
