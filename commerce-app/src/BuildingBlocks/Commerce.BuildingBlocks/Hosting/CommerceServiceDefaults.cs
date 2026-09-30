// Defaults every commerce deployable shares: telemetry, log redaction, security events,
// problem details, response masking, authentication, health endpoints and the standard
// middleware order. Each Program.cs calls these two methods and then adds what is
// specific to that service.
using System.Text.Json.Serialization.Metadata;
using Commerce.BuildingBlocks.Classification;
using Commerce.BuildingBlocks.Health;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Telemetry;
using Commerce.BuildingBlocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Commerce.BuildingBlocks.Hosting;

public static class CommerceServiceDefaults
{
    public static WebApplicationBuilder AddCommerceServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // 1 MiB by default; the document upload endpoint raises its own limit.
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;
        });

        builder.AddCommerceTelemetry(serviceName);
        builder.AddCommerceRedaction();

        var services = builder.Services;
        services.AddSingleton(new ServiceIdentity(serviceName));
        services.AddSingleton<SecurityEventLog>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddSingleton<ClassificationMasking>();
        services.AddOptions<JsonOptions>().Configure<ClassificationMasking>((options, masking) =>
            options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { masking.Apply } });
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            // Problem responses carry a trace id for support, never exception details.
            context.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString();
        });
        services.AddHealthChecks();
        services.AddCommerceAuthentication(builder.Configuration);
        return builder;
    }

    public static WebApplication UseCommerceServiceDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCorrelationIds();
        app.UseApiSecurityHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCommerceHealth();
        return app;
    }
}
