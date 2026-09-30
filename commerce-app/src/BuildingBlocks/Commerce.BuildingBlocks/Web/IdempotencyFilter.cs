// Idempotency keys for state-changing requests that clients may retry (placing an order).
//
// The client sends `Idempotency-Key: <uuid>`. The first request with a key runs and its
// response is stored in Redis for 24 hours, keyed by caller and key. A retry with the same
// key gets the stored response instead of creating a second order; a retry that arrives
// while the first is still running gets 409.
using System.Text.Json;
using Commerce.BuildingBlocks.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Commerce.BuildingBlocks.Web;

public sealed class IdempotencyFilter(IConnectionMultiplexer redis) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly TimeSpan InFlight = TimeSpan.FromMinutes(2);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.Headers[HeaderName], out var key))
        {
            return TypedResults.Problem(title: "idempotency.key-required", detail: $"Send a UUID in the {HeaderName} header.", statusCode: StatusCodes.Status400BadRequest);
        }

        var caller = http.User.FindFirst(KeycloakClaims.Subject)?.Value ?? "anonymous";
        var redisKey = $"idempotency:{caller}:{http.Request.Path}:{key:N}";
        var database = redis.GetDatabase();

        // Claim the key atomically; only the first request gets to run.
        if (!await database.StringSetAsync(redisKey, "in-flight", InFlight, When.NotExists))
        {
            var stored = await database.StringGetAsync(redisKey);
            if (stored == "in-flight")
            {
                return TypedResults.Problem(title: "idempotency.in-progress", detail: "A request with this key is still being processed.", statusCode: StatusCodes.Status409Conflict);
            }

            var snapshot = JsonSerializer.Deserialize<StoredResponse>(stored.ToString())!;
            http.Response.Headers["Idempotent-Replay"] = "true";
            return TypedResults.Json(JsonDocument.Parse(snapshot.Body).RootElement, statusCode: snapshot.Status);
        }

        try
        {
            var result = await next(context);
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 } success && result is IValueHttpResult valueResult)
            {
                var body = JsonSerializer.Serialize(valueResult.Value, http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions);
                await database.StringSetAsync(redisKey, JsonSerializer.Serialize(new StoredResponse(success.StatusCode ?? 200, body)), Retention);
            }
            else
            {
                // Failed requests may be retried with the same key.
                await database.KeyDeleteAsync(redisKey);
            }

            return result;
        }
        catch
        {
            await database.KeyDeleteAsync(redisKey);
            throw;
        }
    }

    private sealed record StoredResponse(int Status, string Body);
}

public static class IdempotencyExtensions
{
    public static RouteHandlerBuilder RequireIdempotencyKey(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<IdempotencyFilter>();
}
