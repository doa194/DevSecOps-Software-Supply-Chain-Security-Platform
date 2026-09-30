// Wires the infrastructure adapters and the application services into dependency injection.
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Application.Builds;
using Sscp.ControlPlane.Application.Evidence;
using Sscp.ControlPlane.Application.Evidence.Readers;
using Sscp.ControlPlane.Application.Exceptions;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Application.Releases;
using Sscp.ControlPlane.Application.Trust;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Infrastructure.Gitea;
using Sscp.ControlPlane.Infrastructure.Persistence;
using Sscp.ControlPlane.Infrastructure.Policy;
using Sscp.ControlPlane.Infrastructure.Queries;
using Sscp.ControlPlane.Infrastructure.Storage;
using Sscp.ControlPlane.Infrastructure.Vault;

namespace Sscp.ControlPlane.Infrastructure;

public static class InfrastructureRegistration
{
    public static DbContextOptionsBuilder ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations", ControlPlaneDbContext.Schema))
            .UseSnakeCaseNamingConvention();

    public static IServiceCollection AddControlPlane(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("controlplane")
            ?? throw new InvalidOperationException("ConnectionStrings:controlplane is required.");
        services.AddDbContext<ControlPlaneDbContext>(options => ConfigureDatabase(options, connection));
        services.AddScoped<IControlPlaneStore, EfControlPlaneStore>();
        services.AddScoped<ControlPlaneQueries>();
        services.AddSingleton(TimeProvider.System);

        var policy = configuration.GetSection(PolicyOptions.SectionName).Get<PolicyOptions>() ?? new PolicyOptions();
        services.AddSingleton(policy);
        services.AddSingleton<IPolicyProvider, YamlPolicyProvider>();
        services.AddSingleton<IApplicationCatalog, YamlApplicationCatalog>();

        var store = configuration.GetSection(EvidenceStoreOptions.SectionName).Get<EvidenceStoreOptions>() ?? new EvidenceStoreOptions();
        services.AddSingleton(store);
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(new BasicAWSCredentials(store.AccessKey, store.SecretKey), new AmazonS3Config
        {
            ServiceURL = store.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            HttpClientFactory = new LocalCaHttpClientFactory(store.CaCertificatePath),
        }));
        services.AddSingleton<IEvidenceStore, S3EvidenceStore>();

        services.AddSingleton<IEvidenceReader, GitleaksReader>();
        services.AddSingleton<IEvidenceReader, SarifReader>();
        services.AddSingleton<IEvidenceReader>(sp => new CheckovReader(sp.GetRequiredService<IPolicyProvider>().InfrastructureSeverities));
        services.AddSingleton<IEvidenceReader>(sp => new CheckovReader(sp.GetRequiredService<IPolicyProvider>().InfrastructureSeverities, EvidenceKind.DeploymentConfigScan));
        services.AddSingleton<IEvidenceReader, HadolintReader>();
        services.AddSingleton<IEvidenceReader, SonarQualityGateReader>();
        services.AddSingleton<IEvidenceReader, CycloneDxReader>();
        services.AddSingleton<IEvidenceReader, TrivyReader>();
        services.AddSingleton<IEvidenceReader, GrypeReader>();
        services.AddSingleton<IEvidenceReader, ZapReader>();
        services.AddSingleton<IEvidenceReader, SecurityTestsReader>();
        services.AddSingleton<EvidenceReaders>();

        services.AddScoped<BuildService>();
        services.AddScoped<EvidenceService>();
        services.AddScoped<TrustService>();
        services.AddScoped<ExceptionService>();
        services.AddScoped<ReleaseService>();

        var gitea = configuration.GetSection(GiteaOptions.SectionName).Get<GiteaOptions>() ?? new GiteaOptions();
        services.AddSingleton(gitea);
        services.AddHttpClient<IPipelinePlatform, GiteaPipelinePlatform>(client =>
        {
            client.BaseAddress = new Uri(gitea.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", gitea.Token);
        }).ConfigurePrimaryHttpMessageHandler(() => LocalCaHttpClientFactory.CreateHandler(gitea.CaCertificatePath));
        services.AddHttpClient<IDesiredStateReader, GiteaDesiredStateReader>(client =>
        {
            client.BaseAddress = new Uri(gitea.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", gitea.Token);
        }).ConfigurePrimaryHttpMessageHandler(() => LocalCaHttpClientFactory.CreateHandler(gitea.CaCertificatePath));
        services.AddScoped<OrchestrationService>();

        var vault = configuration.GetSection(VaultOptions.SectionName).Get<VaultOptions>() ?? new VaultOptions();
        services.AddSingleton(vault);
        services.AddHttpClient<ISigningGrantIssuer, VaultSigningGrantIssuer>(client =>
        {
            client.BaseAddress = new Uri(vault.Address.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(15);
        }).ConfigurePrimaryHttpMessageHandler(() => LocalCaHttpClientFactory.CreateHandler(vault.CaCertificatePath));
        services.AddScoped<ReleaseSigning>();
        return services;
    }
}
