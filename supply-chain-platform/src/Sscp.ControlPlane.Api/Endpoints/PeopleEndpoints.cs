// Endpoints for people: risk-exception workflow and read-only traceability views.
//
// Risk owners request exceptions; a different security approver decides. Everyone with a
// platform role can read builds, evidence, decisions and the audit log, and can re-verify
// the audit hash chain and stored report hashes themselves.
using System.Security.Cryptography;
using Sscp.ControlPlane.Api.Http;
using Sscp.ControlPlane.Api.Security;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Application.Exceptions;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Infrastructure.Queries;

namespace Sscp.ControlPlane.Api.Endpoints;

public sealed record ExceptionRequest(
    string Application, string? Deployable, string Finding, string Kind, string Justification, string? CompensatingControls, DateTimeOffset ExpiresAt);

public sealed record DecisionNote(string? Note);

public static class PeopleEndpoints
{
    public static IEndpointRouteBuilder MapPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        var exceptions = app.MapGroup("/api/exceptions").WithTags("Risk exceptions");

        exceptions.MapPost("/", async (ExceptionRequest body, ExceptionService service, HttpContext http, CancellationToken ct) =>
        {
            if (!Enum.TryParse<EvidenceKind>(body.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || char.IsDigit(body.Kind[0]))
            {
                return OutcomeHttp.Problem(DomainError.Validation("exception.kind.invalid", $"'{body.Kind}' is not an evidence kind."));
            }

            var outcome = await service.RequestAsync(
                new RequestExceptionCommand(body.Application, body.Deployable, body.Finding, kind, body.Justification, body.CompensatingControls, body.ExpiresAt),
                Identity(http), ct);
            return outcome.Succeeded ? Results.Created($"/api/exceptions/{outcome.Value.Id}", Views.Exception(outcome.Value)) : OutcomeHttp.Problem(outcome.Error!);
        }).RequireAuthorization(Policies.RiskOwner);

        exceptions.MapPost("/{id:guid}/approval", async (Guid id, DecisionNote body, ExceptionService service, HttpContext http, CancellationToken ct) =>
            (await service.ApproveAsync(id, Identity(http), body.Note, ct)).ToHttp()).RequireAuthorization(Policies.SecurityApprover);

        exceptions.MapPost("/{id:guid}/rejection", async (Guid id, DecisionNote body, ExceptionService service, HttpContext http, CancellationToken ct) =>
            (await service.RejectAsync(id, Identity(http), body.Note, ct)).ToHttp()).RequireAuthorization(Policies.SecurityApprover);

        exceptions.MapPost("/{id:guid}/revocation", async (Guid id, DecisionNote body, ExceptionService service, HttpContext http, CancellationToken ct) =>
            (await service.RevokeAsync(id, Identity(http), body.Note, ct)).ToHttp()).RequireAuthorization(Policies.SecurityApprover);

        exceptions.MapGet("/", async (string? application, string? status, ControlPlaneQueries queries, CancellationToken ct) =>
        {
            ExceptionStatus? filter = Enum.TryParse<ExceptionStatus>(status, true, out var parsed) ? parsed : null;
            return Results.Ok((await queries.ExceptionsAsync(application, filter, ct)).Select(Views.Exception));
        }).RequireAuthorization(Policies.Viewer);

        // Operators re-run a pipeline that failed for infrastructure reasons.
        app.MapPost("/api/builds/{id:guid}/retry", async (Guid id, OrchestrationService orchestration, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await orchestration.RetryBuildAsync(id, Identity(http), ct);
            return outcome.Succeeded ? Results.Accepted($"/api/builds/{outcome.Value.Id}", Views.Build(outcome.Value)) : OutcomeHttp.Problem(outcome.Error!);
        }).RequireAuthorization(Policies.PlatformAdmin).WithTags("Operations");

        var reads = app.MapGroup("/api").RequireAuthorization(Policies.Viewer).WithTags("Traceability");

        reads.MapGet("/builds", async (string? application, int? limit, ControlPlaneQueries queries, CancellationToken ct) =>
            Results.Ok((await queries.RecentBuildsAsync(application, limit ?? 20, ct)).Select(Views.Build)));

        reads.MapGet("/builds/{id:guid}", async (Guid id, ControlPlaneQueries queries, CancellationToken ct) =>
        {
            var build = await queries.BuildAsync(id, ct);
            return build is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    build = Views.Build(build),
                    artifacts = (await queries.ArtifactsForBuildAsync(id, ct)).Select(Views.Artifact),
                    evidence = await queries.EvidenceForBuildAsync(id, ct),
                });
        });

        reads.MapGet("/artifacts/{digest}/trace", async (string digest, ControlPlaneQueries queries, CancellationToken ct) =>
        {
            if (!Digest.Parse(digest).Succeeded)
            {
                return OutcomeHttp.Problem(DomainError.Validation("digest.invalid", "Expected sha256:<64 hex characters>."));
            }

            var traces = await queries.TraceAsync(digest, ct);
            return traces.Count == 0
                ? Results.NotFound()
                : Results.Ok(traces.Select(t => new
                {
                    artifact = Views.Artifact(t.Artifact),
                    build = Views.Build(t.Build),
                    evidence = t.Evidence,
                    decisions = t.Decisions.Select(Views.Decision),
                    signatures = t.Signatures,
                    promotions = t.Promotions,
                    releases = t.Releases.Select(Views.Release),
                    deployments = t.Deployments,
                }));
        });

        reads.MapGet("/releases/{id:guid}", async (Guid id, ControlPlaneQueries queries, CancellationToken ct) =>
            await queries.ReleaseAsync(id, ct) is { } release ? Results.Ok(Views.Release(release)) : Results.NotFound());

        // Returns the stored raw report after checking it still matches the recorded hash.
        reads.MapGet("/evidence/{id:guid}/report", async (Guid id, ControlPlaneQueries queries, IEvidenceStore store, CancellationToken ct) =>
        {
            var evidence = await queries.EvidenceAsync(id, ct);
            var content = evidence is null ? null : await store.ReadAsync(evidence.Report.ObjectKey, ct);
            if (evidence is null || content is null)
            {
                return Results.NotFound();
            }

            return Convert.ToHexStringLower(SHA256.HashData(content)) != evidence.Report.Sha256
                ? OutcomeHttp.Problem(DomainError.Conflict("evidence.integrity", "The stored report no longer matches its recorded SHA-256."))
                : Results.File(content, evidence.Report.ContentType, $"{evidence.Kind}-{evidence.Id:N}");
        });

        reads.MapGet("/audit", async (string? subjectType, string? subjectId, int? limit, ControlPlaneQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.AuditAsync(subjectType, subjectId, limit ?? 100, ct)));

        reads.MapGet("/audit/verification", async (ControlPlaneQueries queries, CancellationToken ct) =>
            Results.Ok(await queries.VerifyAuditChainAsync(ct)));

        reads.MapGet("/policy", (IPolicyProvider policies) => Results.Ok(new
        {
            policies.Current.Version,
            mandatoryEvidence = new
            {
                commit = policies.Current.MandatoryCommitEvidence.Select(k => k.ToString()),
                artifact = policies.Current.MandatoryArtifactEvidence.Select(k => k.ToString()),
                pullRequest = policies.Current.MandatoryPullRequestEvidence.Select(k => k.ToString()),
            },
            gates = new
            {
                staticAnalysis = policies.Current.StaticAnalysis,
                infrastructure = policies.Current.Infrastructure,
                dockerfile = policies.Current.DockerfileLint,
                dynamicScan = policies.Current.DynamicScan,
            },
            vulnerabilitySla = policies.Current.VulnerabilitySla,
            maxVulnerabilityDatabaseAgeDays = policies.Current.MaxVulnerabilityDatabaseAge.TotalDays,
            exceptions = new
            {
                policies.Current.Exceptions.MaxDays,
                policies.Current.Exceptions.MinimumJustificationLength,
                allowedKinds = policies.Current.Exceptions.AllowedKinds.Select(k => k.ToString()),
            },
        }));

        return app;
    }

    private static string Identity(HttpContext http) => CallerResolver.Resolve(http.User).Identity;
}
