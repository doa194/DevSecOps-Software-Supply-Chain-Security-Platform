// Health checks, Prometheus metrics, telemetry and error handling for the Control Plane.
//
// Health and metrics are served only on the management port (plain HTTP, never published
// on the host), and the API is served only on the other ports. This keeps unauthenticated
// endpoints off the API listener and keeps bearer tokens off the plain-HTTP listener.
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Infrastructure.Persistence;
using Sscp.ControlPlane.Infrastructure.Storage;

namespace Sscp.ControlPlane.Api.Hosting;

public sealed class ManagementOptions
{
    public const string SectionName = "Management";
    public int Port { get; set; } = 9464;
}

public static class ManagementEndpoints
{
    private static readonly string[] ManagementPaths = ["/health", "/metrics"];

    public static IServiceCollection AddControlPlaneHealth(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<EvidenceStoreHealthCheck>("evidence-store", tags: ["ready"]);
        return services;
    }

    public static WebApplicationBuilder AddControlPlaneTelemetry(this WebApplicationBuilder builder)
    {
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("sscp-controlplane"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(ControlPlaneMetrics.MeterName)
                .AddPrometheusExporter())
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsManagementPath(context.Request.Path))
                .AddHttpClientInstrumentation());

        // Traces leave the process only when a collector is configured.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.WithTracing(tracing => tracing.AddOtlpExporter());
        }

        return builder;
    }

    public static IApplicationBuilder UseManagementPortIsolation(this WebApplication app)
    {
        var port = app.Services.GetRequiredService<ManagementOptions>().Port;
        return app.Use(async (context, next) =>
        {
            var onManagementPort = context.Connection.LocalPort == port;
            if (onManagementPort != IsManagementPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
    }

    public static WebApplication MapManagementEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
        app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
        return app;
    }

    public static async Task<int> ProbeAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health/ready"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }

    private static bool IsManagementPath(PathString path) => ManagementPaths.Any(prefix => path.StartsWithSegments(prefix));
}

internal sealed class DatabaseHealthCheck(ControlPlaneDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("database unreachable");
}

internal sealed class EvidenceStoreHealthCheck(IAmazonS3 s3, EvidenceStoreOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Bucket, MaxKeys = 1 }, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (AmazonS3Exception error)
        {
            return HealthCheckResult.Unhealthy("evidence bucket unavailable", error);
        }
        catch (HttpRequestException error)
        {
            return HealthCheckResult.Unhealthy("evidence store unreachable", error);
        }
    }
}

// Two pipeline jobs changing the same artifact at the same moment: the later one gets a
// 409 and can retry, instead of silently overwriting the first change.
internal sealed class ConcurrencyConflictHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
        {
            return false;
        }

        await Results.Problem(title: "The record was changed by another request; retry.", statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = "concurrency.conflict" }).ExecuteAsync(httpContext);
        return true;
    }
}
