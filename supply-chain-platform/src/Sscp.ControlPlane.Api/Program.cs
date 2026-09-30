// Host of the Security Control Plane.
//
// Modes:
//   (no argument)  serve the API. HTTPS on the API port for pipelines and people; plain
//                  HTTP on the management port for health checks and Prometheus only.
//   migrate        apply database migrations as the owner role, then grant the runtime
//                  role only the row access it needs.
//   healthcheck    call the local readiness endpoint and exit 0 or 1. The container
//                  health check uses this because the chiseled runtime image has no shell
//                  or curl.
using System.Text.Json.Serialization;
using Sscp.ControlPlane.Api.Background;
using Sscp.ControlPlane.Api.Endpoints;
using Sscp.ControlPlane.Api.Hosting;
using Sscp.ControlPlane.Api.Security;
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Infrastructure;
using Sscp.ControlPlane.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
var management = builder.Configuration.GetSection(ManagementOptions.SectionName).Get<ManagementOptions>() ?? new ManagementOptions();

if (args.Contains("healthcheck"))
{
    return await ManagementEndpoints.ProbeAsync(management.Port);
}

if (args.Contains("migrate"))
{
    var owner = builder.Configuration.GetConnectionString("migrations")
        ?? throw new InvalidOperationException("ConnectionStrings:migrations (database owner) is required for migrate.");
    await ControlPlaneMigrator.MigrateAsync(owner, builder.Configuration["Database:RuntimeRole"] ?? "controlplane_app", CancellationToken.None);
    return 0;
}

builder.Services.AddControlPlane(builder.Configuration);
builder.Services.AddControlPlaneAuth(builder.Configuration);
builder.Services.AddSingleton(management);
builder.Services.AddSingleton(builder.Configuration.GetSection(BackgroundOptions.SectionName).Get<BackgroundOptions>() ?? new BackgroundOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection(ArgoCdOptions.SectionName).Get<ArgoCdOptions>() ?? new ArgoCdOptions());
builder.Services.AddHostedService<ExceptionExpiryJob>();
builder.Services.AddHostedService<StateGaugeJob>();
builder.Services.AddHostedService<RunWatcherJob>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConcurrencyConflictHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddControlPlaneHealth();
builder.AddControlPlaneTelemetry();

var app = builder.Build();

// Measurements count only once the metrics pipeline listens, which is after start-up.
app.Lifetime.ApplicationStarted.Register(ControlPlaneMetrics.PublishKnownSeries);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseManagementPortIsolation();
app.UseAuthentication();
app.UseAuthorization();

app.MapManagementEndpoints();
app.MapPipelineEndpoints();
app.MapPeopleEndpoints();
app.MapWebhookEndpoints();
app.MapDeploymentEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
