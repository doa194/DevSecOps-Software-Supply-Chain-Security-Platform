// Liveness and readiness.
//
// /health/live   the process is running (Kubernetes restarts the pod if this fails)
// /health/ready  every dependency the service needs right now answers (Kubernetes stops
//                routing traffic until it does), e.g. its database schema and RabbitMQ
using Commerce.BuildingBlocks.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Commerce.BuildingBlocks.Health;

public static class HealthSetup
{
    public const string ReadyTag = "ready";

    public static IHealthChecksBuilder AddRabbitMqReadiness(this IHealthChecksBuilder builder) =>
        builder.AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: [ReadyTag]);

    public static IHealthChecksBuilder AddRedisReadiness(this IHealthChecksBuilder builder) =>
        builder.AddCheck<RedisHealthCheck>("redis", tags: [ReadyTag]);

    public static IEndpointRouteBuilder MapCommerceHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return endpoints;
    }
}

internal sealed class RabbitMqHealthCheck(RabbitMqConnectionProvider connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await connections.GetConnectionAsync(cancellationToken);
            return connection.IsOpen ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("connection closed");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("broker unreachable", error);
        }
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("redis unreachable", error);
        }
    }
}
