// YARP API gateway: the only public entry point of the commerce workload.
//
// Responsibilities at the boundary (backends still validate tokens themselves; this is
// defence in depth, not the only check):
// - authenticate every request except an explicit allow-list of public catalog routes,
// - rate-limit per user (or per client address when anonymous) and report rejections as
//   security events,
// - cap request sizes (documents may be larger than other requests),
// - overwrite client-supplied X-Forwarded-* headers instead of trusting them,
// - add security headers and a correlation id to every response.
using System.Threading.RateLimiting;
using Commerce.BuildingBlocks.Hosting;
using Commerce.BuildingBlocks.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommerceServiceDefaults("commerce-gateway");

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        // Replace, never append: a client cannot inject its own X-Forwarded-For chain.
        context.AddXForwarded(ForwardedTransformActions.Set);
    });

var limits = builder.Configuration.GetSection("RateLimits");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        var http = context.HttpContext;
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "route";
        http.RequestServices.GetRequiredService<SecurityEventLog>().RateLimited(
            http.User.FindFirst(KeycloakClaims.Subject)?.Value, http.Connection.RemoteIpAddress?.ToString(), http.Request.Path, policy);
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("anonymous", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => Window(limits.GetValue("AnonymousPerMinute", 60))));
    options.AddPolicy("user", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(KeycloakClaims.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => Window(limits.GetValue("UserPerMinute", 300))));
    // Placing orders is the most abuse-prone operation, so it has its own lower limit.
    options.AddPolicy("order-placement", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(KeycloakClaims.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => Window(limits.GetValue("OrdersPerMinute", 10))));
});

var app = builder.Build();

app.UseCommerceServiceDefaults();

// Request size limits per route family, enforced before anything is proxied.
app.Use(async (context, next) =>
{
    var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false })
    {
        var isUpload = HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/documents");
        feature.MaxRequestBodySize = isUpload ? 6 * 1024 * 1024 : 1024 * 1024;
    }

    await next(context);
});

app.UseRateLimiter();
app.MapReverseProxy();

await app.RunAsync();

static FixedWindowRateLimiterOptions Window(int permitsPerMinute) => new()
{
    PermitLimit = permitsPerMinute,
    Window = TimeSpan.FromMinutes(1),
    QueueLimit = 0,
};

public partial class Program;
