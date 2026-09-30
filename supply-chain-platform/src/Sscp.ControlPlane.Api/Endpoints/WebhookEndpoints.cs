// Receives Gitea webhooks from application repositories.
//
// This endpoint takes no bearer token: it is authenticated by the HMAC signature Gitea
// computes with the shared webhook secret. Deliveries with a missing or wrong signature
// are refused and logged. Re-delivered events are harmless because the orchestration is
// idempotent per commit and tag.
using Microsoft.AspNetCore.Mvc;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Infrastructure.Gitea;

namespace Sscp.ControlPlane.Api.Endpoints;

public static partial class WebhookEndpoints
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/gitea", ReceiveAsync)
            .AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .WithTags("Webhooks");
        return app;
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, GiteaOptions options, OrchestrationService orchestration,
        ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var body = buffer.ToArray();

        if (!GiteaWebhook.HasValidSignature(body, request.Headers["X-Gitea-Signature"], options.WebhookSecret))
        {
            LogRejected(loggers.CreateLogger("Sscp.Security"), request.HttpContext.Connection.RemoteIpAddress?.ToString(), request.Headers["X-Gitea-Event"]);
            return Results.Unauthorized();
        }

        SourceEvent? sourceEvent;
        try
        {
            sourceEvent = GiteaWebhook.Parse(request.Headers["X-Gitea-Event"], body);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.BadRequest();
        }
        catch (KeyNotFoundException)
        {
            return Results.BadRequest();
        }

        if (sourceEvent is null)
        {
            return Results.NoContent();
        }

        await orchestration.HandleAsync(sourceEvent, cancellationToken);
        return Results.Accepted();
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Warning, Message = "Webhook rejected: invalid signature (source {Source}, event {Event})")]
    private static partial void LogRejected(ILogger logger, string? source, string? @event);
}
