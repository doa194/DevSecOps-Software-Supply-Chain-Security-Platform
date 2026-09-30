// Endpoints called by the CI trust zones while a build or release pipeline runs.
//
// Every call carries the pipeline run id; the application services accept it only when it
// is the run the Control Plane dispatched for that build or release, and only for the
// actions the caller's zone is allowed to perform.
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Sscp.ControlPlane.Api.Http;
using Sscp.ControlPlane.Api.Security;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Application.Builds;
using Sscp.ControlPlane.Application.Evidence;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Application.Releases;
using Sscp.ControlPlane.Application.Trust;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Api.Endpoints;

public sealed record RunRequest(long RunId);
public sealed record CompletionRequest(long RunId, bool Succeeded, string? Reason);
public sealed record ArtifactRegistration(long RunId, string Deployable, string Repository, string Digest);
public sealed record SignatureRequest(long RunId, Guid ArtifactId, string KeyReference, string SignatureReference, IReadOnlyList<string>? AttestationReferences);
public sealed record PromotionRequest(long RunId, Guid ArtifactId, string TrustedRepository);
public sealed record GitOpsRequest(long RunId, string Commit);
public sealed record FailureRequest(long RunId, string Reason);

public static class PipelineEndpoints
{
    // Largest accepted raw report. SBOMs and vulnerability reports of the workload images
    // are a few megabytes; the limit leaves ample room without accepting unbounded uploads.
    public const long MaxReportBytes = 64L * 1024 * 1024;
    private const int MaxMetadataEntries = 50;

    public static IEndpointRouteBuilder MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        var builds = app.MapGroup("/api/builds/{buildId:guid}").RequireAuthorization(Policies.Pipeline).WithTags("Pipeline");

        // What the pipeline must build and scan: the registered deployables (the platform's
        // list, not the application repository's) and the candidates registered so far.
        builds.MapGet("/plan", async (Guid buildId, IControlPlaneStore store, IApplicationCatalog applications, CancellationToken ct) =>
        {
            if (await store.BuildAsync(buildId, ct) is not { } build || applications.Find(build.Application) is not { } application)
            {
                return Results.NotFound();
            }

            var artifacts = await store.ArtifactsForBuildAsync(buildId, ct);
            return Results.Ok(new
            {
                build.Application, build.Commit, build.SourceRepository, kind = build.Kind.ToString(),
                application.Deployables, candidateRepository = application.CandidateRepositoryPrefix,
                artifacts = artifacts.Select(a => new { a.Id, a.Deployable, a.Digest, reference = a.CandidateReference }),
            });
        });

        builds.MapPost("/artifacts", async (Guid buildId, ArtifactRegistration body, BuildService service, HttpContext http, CancellationToken ct) =>
            (await service.RegisterArtifactAsync(new RegisterArtifactCommand(buildId, body.RunId, body.Deployable, body.Repository, body.Digest), Caller(http), ct))
                .ToHttp(Views.Artifact));

        builds.MapPost("/evidence", SubmitEvidenceAsync)
            .WithMetadata(new RequestSizeLimitAttribute(MaxReportBytes + (1024 * 1024)));

        // After each decision the Control Plane itself sets the commit status developers see.
        builds.MapPost("/evaluation", async (Guid buildId, RunRequest body, TrustService service, OrchestrationService orchestration, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await service.EvaluateBuildAsync(buildId, body.RunId, Caller(http), ct);
            if (outcome.Succeeded)
            {
                await orchestration.ReportBuildDecisionAsync(buildId, outcome.Value, ct);
            }

            return outcome.ToHttp(decisions => decisions.Select(Views.ArtifactDecision));
        });

        builds.MapPost("/source-evaluation", async (Guid buildId, RunRequest body, TrustService service, OrchestrationService orchestration, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await service.EvaluateSourceAsync(buildId, body.RunId, Caller(http), ct);
            if (outcome.Succeeded)
            {
                await orchestration.ReportSourceDecisionAsync(buildId, outcome.Value, ct);
            }

            return outcome.ToHttp(Views.SourceDecision);
        });

        builds.MapPost("/completion", async (Guid buildId, CompletionRequest body, BuildService service, OrchestrationService orchestration, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await service.CompleteAsync(buildId, body.RunId, body.Succeeded, body.Reason, Caller(http), ct);
            if (outcome.Succeeded && !body.Succeeded)
            {
                await orchestration.ReportBuildFailureAsync(buildId, body.Reason, ct);
            }

            return outcome.ToHttp();
        });

        var releases = app.MapGroup("/api/releases/{releaseId:guid}").RequireAuthorization(Policies.Pipeline).WithTags("Pipeline");

        // What the release pipeline must sign and promote: the artifacts of the build the
        // release was cut from, and where trusted copies must go.
        releases.MapGet("/plan", async (Guid releaseId, IControlPlaneStore store, IApplicationCatalog applications, CancellationToken ct) =>
        {
            if (await store.ReleaseAsync(releaseId, ct) is not { } release || applications.Find(release.Application) is not { } application)
            {
                return Results.NotFound();
            }

            var artifacts = new List<object>();
            foreach (var id in release.ArtifactIds)
            {
                var artifact = (await store.ArtifactAsync(id, ct))!;
                artifacts.Add(new { artifact.Id, artifact.Deployable, artifact.Digest, reference = artifact.CandidateReference, state = artifact.State.ToString() });
            }

            return Results.Ok(new
            {
                release.Id, release.Application, release.Tag, release.Commit, state = release.State.ToString(),
                trustedRepository = application.TrustedRepositoryPrefix, artifacts,
            });
        });

        releases.MapPost("/evaluation", async (Guid releaseId, RunRequest body, ReleaseService service, OrchestrationService orchestration, HttpContext http, CancellationToken ct) =>
        {
            var outcome = await service.EvaluateAsync(releaseId, body.RunId, Caller(http), ct);
            if (outcome.Succeeded)
            {
                await orchestration.ReportReleaseDecisionAsync(outcome.Value, ct);
            }

            return outcome.ToHttp(Views.ReleaseDecision);
        });

        // The grant is a secret for the next few minutes: it must never be cached.
        releases.MapPost("/signing-grant", async (Guid releaseId, RunRequest body, ReleaseSigning signing, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return (await signing.IssueGrantAsync(releaseId, body.RunId, Caller(http), ct))
                .ToHttp(grant => new { roleId = grant.RoleId, wrappedSecretId = grant.WrappedSecretId, expiresAt = grant.ExpiresAt });
        });

        releases.MapGet("/artifacts/{artifactId:guid}/attestations", async (Guid releaseId, Guid artifactId, long runId, ReleaseSigning signing, HttpContext http, CancellationToken ct) =>
            (await signing.MaterialAsync(releaseId, artifactId, runId, Caller(http), ct))
                .ToHttp(material => new
                {
                    provenance = new { predicateType = ReleaseSigning.ProvenancePredicateType, predicate = material.Provenance },
                    trustDecision = new { predicateType = ReleaseSigning.TrustDecisionPredicateType, predicate = material.TrustDecision },
                    sbom = new { predicateType = "cyclonedx", predicate = material.Sbom },
                }));

        releases.MapPost("/signatures", async (Guid releaseId, SignatureRequest body, ReleaseService service, HttpContext http, CancellationToken ct) =>
            (await service.RecordSignatureAsync(releaseId,
                new SignatureCommand(body.ArtifactId, body.KeyReference, body.SignatureReference, body.AttestationReferences ?? [], body.RunId), Caller(http), ct)).ToHttp());

        releases.MapPost("/promotions", async (Guid releaseId, PromotionRequest body, ReleaseService service, HttpContext http, CancellationToken ct) =>
            (await service.RecordPromotionAsync(releaseId, new PromotionCommand(body.ArtifactId, body.TrustedRepository, body.RunId), Caller(http), ct)).ToHttp());

        releases.MapPost("/gitops", async (Guid releaseId, GitOpsRequest body, ReleaseService service, HttpContext http, CancellationToken ct) =>
            (await service.RecordGitOpsChangeAsync(releaseId, body.RunId, body.Commit, Caller(http), ct)).ToHttp());

        releases.MapPost("/failure", async (Guid releaseId, FailureRequest body, ReleaseService service, HttpContext http, CancellationToken ct) =>
            (await service.FailAsync(releaseId, body.RunId, body.Reason, Caller(http), ct)).ToHttp());

        return app;
    }

    // multipart/form-data: the raw report as file part `report`, the rest as form fields.
    // The form is read explicitly (not model-bound) because this is a bearer-token API with
    // no cookies, so browser anti-forgery tokens do not apply.
    private static async Task<IResult> SubmitEvidenceAsync(Guid buildId, HttpRequest request, EvidenceService service, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return OutcomeHttp.Problem(DomainError.Validation("evidence.form.required", "Evidence must be sent as multipart/form-data."));
        }

        var form = await request.ReadFormAsync(ct);
        var report = form.Files.GetFile("report");
        if (report is null || report.Length == 0 || report.Length > MaxReportBytes)
        {
            return OutcomeHttp.Problem(DomainError.Validation("evidence.report.missing", $"A non-empty report file of at most {MaxReportBytes} bytes is required."));
        }

        if (!long.TryParse(form["runId"], out var runId)
            || !TryParseEnum<EvidenceKind>(form["kind"], out var kind)
            || !TryParseEnum<ExecutionStatus>(form["execution"], out var execution))
        {
            return OutcomeHttp.Problem(DomainError.Validation("evidence.form.invalid", "runId, kind and execution are required and must be valid."));
        }

        var metadata = ParseMetadata(form["metadata"]);
        if (metadata is null)
        {
            return OutcomeHttp.Problem(DomainError.Validation("evidence.metadata.invalid", $"metadata must be a JSON object of at most {MaxMetadataEntries} string values."));
        }

        byte[] content;
        await using (var stream = report.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }

        var command = new SubmitEvidenceCommand(buildId, runId, Text(form["jobName"]) ?? "unknown", kind, Text(form["commit"]) ?? string.Empty,
            Text(form["deployable"]), Text(form["digest"]), execution, Text(form["executionError"]), metadata, content,
            string.IsNullOrWhiteSpace(report.ContentType) ? "application/octet-stream" : report.ContentType);
        var outcome = await service.SubmitAsync(command, Caller(request.HttpContext), ct);
        return outcome.Succeeded
            ? Results.Created($"/api/evidence/{outcome.Value.Id}", Views.Evidence(outcome.Value))
            : OutcomeHttp.Problem(outcome.Error!);
    }

    private static Application.Caller Caller(HttpContext http) => CallerResolver.Resolve(http.User);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Names only: Enum.TryParse would also accept numbers such as "7".
    private static bool TryParseEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return !string.IsNullOrWhiteSpace(value) && !char.IsDigit(value.Trim()[0]) && Enum.TryParse(value.Trim(), ignoreCase: true, out result) && Enum.IsDefined(result);
    }

    private static Dictionary<string, string>? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return values is not null && values.Count <= MaxMetadataEntries && values.All(pair => pair.Key.Length <= 100 && pair.Value.Length <= 2000)
                ? values
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
