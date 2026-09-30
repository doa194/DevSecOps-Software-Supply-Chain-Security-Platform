// Cross-cutting HTTP behaviour shared by the API, the gateway and the workers' read APIs.
using System.Text.RegularExpressions;
using Commerce.BuildingBlocks.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Commerce.BuildingBlocks.Web;

public static partial class WebDefaults
{
    public const string CorrelationHeader = "X-Correlation-Id";

    // Accepts the caller's correlation id only if it is a short, safe token (it ends up in
    // logs and audit records), otherwise uses the trace id.
    public static IApplicationBuilder UseCorrelationIds(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var supplied = context.Request.Headers[CorrelationHeader].ToString();
        var correlationId = SafeCorrelationId().IsMatch(supplied) ? supplied : System.Diagnostics.Activity.Current?.TraceId.ToString();
        context.Response.Headers[CorrelationHeader] = correlationId;
        using (CorrelationContext.Begin(correlationId))
        {
            await next(context);
        }
    });

    // Headers that stop browsers from sniffing, framing or caching API responses.
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Cache-Control"] = "no-store";
            headers["Strict-Transport-Security"] = "max-age=31536000";
            headers.Remove("Server");
            return Task.CompletedTask;
        });
        await next(context);
    });

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeCorrelationId();
}

public sealed record PageRequest(int Page = 1, int PageSize = 20)
{
    public const int MaxPageSize = 100;
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, MaxPageSize);
    public int Skip => (SafePage - 1) * SafePageSize;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
