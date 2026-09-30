// Runs the real Control Plane host against disposable PostgreSQL and MinIO.
//
// The database is migrated with the owner role and the API connects as the restricted
// runtime role, exactly as deployed, so a missing grant fails these tests. What differs
// from a deployment: tokens are signed by a test key, the clock is a fake the tests can
// move forward, the evidence bucket lives in a throw-away MinIO, and Gitea (Actions and the
// GitOps repository) and Vault are replaced by recorders.
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Application.Releases;
using Sscp.ControlPlane.Infrastructure.Persistence;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Sscp.ControlPlane.ComponentTests.Infrastructure.ControlPlaneFactory))]

namespace Sscp.ControlPlane.ComponentTests.Infrastructure;

public sealed class ControlPlaneFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Bucket = "evidence";
    private const string RuntimePassword = "runtime-secret";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(TestImages.Postgres).Build();
    private readonly MinioContainer _minio = new MinioBuilder(TestImages.Minio).WithUsername("admin").WithPassword("admin-secret").Build();

    public const string WebhookSecret = "component-test-webhook-secret";
    public const string ArgoCdToken = "component-test-argocd-token";

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public RecordingPipelinePlatform Platform { get; } = new();
    public RecordingGrantIssuer Grants { get; } = new();
    public RecordingDesiredState DesiredState { get; } = new();
    public IAmazonS3 StorageAdmin { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());

        await using (var admin = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE controlplane_app LOGIN PASSWORD '{RuntimePassword}'", admin);
            await create.ExecuteNonQueryAsync();
        }

        await ControlPlaneMigrator.MigrateAsync(_postgres.GetConnectionString(), "controlplane_app", CancellationToken.None);

        StorageAdmin = new AmazonS3Client("admin", "admin-secret", new AmazonS3Config { ServiceURL = _minio.GetConnectionString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await StorageAdmin.PutBucketAsync(new PutBucketRequest { BucketName = Bucket, ObjectLockEnabledForBucket = true });
    }

    private string RuntimeConnection => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
    {
        Username = "controlplane_app",
        Password = RuntimePassword,
    }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:controlplane"] = RuntimeConnection,
            ["Identity:Issuer"] = TestTokens.Issuer,
            ["Policy:TrustPolicyPath"] = Path.Combine(AppContext.BaseDirectory, "policy", "trust-policy.yaml"),
            ["Policy:ApplicationsPath"] = Path.Combine(AppContext.BaseDirectory, "policy", "applications.yaml"),
            ["EvidenceStore:Endpoint"] = _minio.GetConnectionString(),
            ["EvidenceStore:Bucket"] = Bucket,
            ["EvidenceStore:AccessKey"] = "admin",
            ["EvidenceStore:SecretKey"] = "admin-secret",
            ["Gitea:WebhookSecret"] = WebhookSecret,
            ["ArgoCd:NotificationToken"] = ArgoCdToken,
        };
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IPipelinePlatform>(Platform);
            services.AddSingleton<ISigningGrantIssuer>(Grants);
            services.AddSingleton<IDesiredStateReader>(DesiredState);
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                configuration.SigningKeys.Add(TestTokens.SigningKey);
                // A static manager: without it the handler would try to download the fake
                // issuer's metadata on every request.
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
            });
        });
    }

    public HttpClient ClientWith(string? token)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        StorageAdmin?.Dispose();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _minio.DisposeAsync().AsTask());
    }
}

internal static class TestImages
{
    // Pinned by digest, matching supply-chain-platform/versions.yaml.
    public const string Postgres = "postgres:18.6-trixie@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722";
    public const string Minio = "cgr.dev/chainguard/minio:latest@sha256:bd014394a80898e68c149f2311fdf8d5a2c2f3bb2c33b9327ae6d02b4b065ae1";
}
