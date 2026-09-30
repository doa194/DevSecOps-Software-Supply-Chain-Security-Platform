// OpenTelemetry for every deployable: traces, metrics and logs exported over OTLP to the
// collector in the cluster. Nothing is exported unless OTEL_EXPORTER_OTLP_ENDPOINT is set,
// so local runs and tests do not try to reach a collector that is not there.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Commerce.BuildingBlocks.Telemetry;

public static class TelemetrySetup
{
    // Meters and activity sources owned by the workload; collected in addition to the
    // standard ASP.NET Core, HTTP client, runtime and Npgsql instrumentation.
    public static readonly string[] Meters = ["Commerce.Security", "Commerce.Messaging", "Commerce.Business"];
    public static readonly string[] Sources = ["Commerce.Messaging", "Commerce.Business"];

    public static IHostApplicationBuilder AddCommerceTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        var exportEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var version = typeof(TelemetrySetup).Assembly.GetName().Version?.ToString() ?? "unknown";

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource(Sources)
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(Meters)
                .AddMeter("Npgsql"));

        if (exportEnabled)
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
