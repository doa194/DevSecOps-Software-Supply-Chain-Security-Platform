// Receives deployment reports from Argo CD's notifications controller.
//
// Argo CD cannot obtain Keycloak tokens, so this endpoint is authenticated by a shared
// bearer token instead: Vault holds it, External Secrets delivers it to Argo CD, and the
// Control Plane gets the same value from its configuration. A report only moves a release
// to Deployed when its revision is the release's GitOps commit and that revision pins the
// approved digests (see ReleaseService.RecordDeploymentAsync).
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Sscp.ControlPlane.Api.Http;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Application.Releases;

namespace Sscp.ControlPlane.Api.Endpoints;

public sealed class ArgoCdOptions
{
    public const string SectionName = "ArgoCd";
    public string NotificationToken { get; set; } = string.Empty;
}

public sealed record ArgoCdDeployment(string? Application, string? Revision, string? SyncStatus, string? Health, IReadOnlyList<string>? Images);

public static partial class DeploymentEndpoints
{
    private const string Actor = "service:argocd";

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/deployments/argocd", ReceiveAsync)
            .AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(256 * 1024))
            .WithTags("Deployments");
        return app;
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, ArgoCdDeployment body, ArgoCdOptions options, ReleaseService releases,
        OrchestrationService orchestration, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!HasValidToken(request.Headers.Authorization, options.NotificationToken))
        {
            LogRejected(loggers.CreateLogger("Sscp.Security"), request.HttpContext.Connection.RemoteIpAddress?.ToString());
            return Results.Unauthorized();
        }

        var report = new DeploymentReport(body.Application ?? string.Empty, body.Revision ?? string.Empty,
            body.SyncStatus ?? string.Empty, body.Health ?? string.Empty, body.Images ?? []);
        var outcome = await releases.RecordDeploymentAsync(report, Actor, cancellationToken);
        if (outcome.Succeeded && outcome.Value == DeploymentResult.Deployed)
        {
            await orchestration.ReportDeploymentAsync(report, cancellationToken);
        }

        return outcome.ToHttp(result => new { result = result.ToString() });
    }

    // No configured token means nothing can be verified: refuse everything.
    private static bool HasValidToken(string? header, string expected)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrEmpty(expected) || header is null || !header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[prefix.Length..]), Encoding.UTF8.GetBytes(expected));
    }

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "Deployment report rejected: invalid token (source {Source})")]
    private static partial void LogRejected(ILogger logger, string? source);
}
