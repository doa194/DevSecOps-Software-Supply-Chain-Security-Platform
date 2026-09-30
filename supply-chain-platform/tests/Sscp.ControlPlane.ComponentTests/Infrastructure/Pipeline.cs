// Drives the Control Plane the way the platform pipelines do: the Control Plane opens a
// build or release and binds a pipeline run to it (in the deployment this happens when it
// dispatches the workflow), then zone clients register artifacts, submit evidence and ask
// for decisions over HTTP.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sscp.ControlPlane.Application.Builds;
using Sscp.ControlPlane.Application.Releases;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.ComponentTests.Infrastructure;

public sealed record RegisteredArtifact(Guid Id, string Deployable, string Repository, string Digest);

public sealed record OpenBuild(Guid Id, string Commit, long RunId, IReadOnlyList<RegisteredArtifact> Artifacts);

public sealed class Pipeline(ControlPlaneFactory factory)
{
    public const string Candidate = "harbor.sscp.test:8443/commerce-candidates";
    public const string Trusted = "harbor.sscp.test:8443/commerce-trusted";
    public static readonly string[] Deployables = ["commerce-api", "commerce-gateway", "notification-worker", "audit-worker", "document-worker", "reporting-worker"];
    public static readonly EvidenceKind[] CommitKinds =
    [
        EvidenceKind.SecretScan, EvidenceKind.StaticAnalysis, EvidenceKind.CodeQuality, EvidenceKind.InfrastructureScan,
        EvidenceKind.DockerfileLint, EvidenceKind.DynamicScan, EvidenceKind.SecurityTests,
    ];

    private static long _nextRun = 1000;

    public HttpClient Security { get; } = factory.ClientWith(TestTokens.Zone("ci-security-zone"));
    public HttpClient Build { get; } = factory.ClientWith(TestTokens.Zone("ci-build-zone"));
    public HttpClient Trust { get; } = factory.ClientWith(TestTokens.Zone("ci-trust-zone"));

    public static string NewCommit() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
    public static string NewDigest() => $"sha256:{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
    public static long NewRunId() => Interlocked.Increment(ref _nextRun);

    // Opens a main build bound to a new run and registers one candidate image per deployable.
    public async Task<OpenBuild> StartMainBuildAsync()
    {
        var commit = NewCommit();
        var runId = NewRunId();
        await using var scope = factory.Services.CreateAsyncScope();
        var builds = scope.ServiceProvider.GetRequiredService<BuildService>();
        var build = (await builds.RequestAsync("commerce", commit, "refs/heads/main", BuildKind.MainBranch, null, "service:controlplane", CancellationToken.None)).Value;
        Assert.True((await builds.AttachRunAsync(build.Id, runId, "service:controlplane", CancellationToken.None)).Succeeded);

        var artifacts = new List<RegisteredArtifact>();
        foreach (var deployable in Deployables)
        {
            var repository = $"{Candidate}/{deployable}";
            var digest = NewDigest();
            var response = await Build.PostAsJsonAsync($"/api/builds/{build.Id}/artifacts", new { runId, deployable, repository, digest });
            await Expect(response, 200);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            artifacts.Add(new RegisteredArtifact(body.GetProperty("id").GetGuid(), deployable, repository, digest));
        }

        return new OpenBuild(build.Id, commit, runId, artifacts);
    }

    public static Task<HttpResponseMessage> SubmitAsync(HttpClient zone, OpenBuild build, EvidenceKind kind, byte[] report, RegisteredArtifact? artifact = null,
        long? runId = null, IReadOnlyDictionary<string, string>? metadata = null, string execution = "Completed")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent((runId ?? build.RunId).ToString(System.Globalization.CultureInfo.InvariantCulture)), "runId" },
            { new StringContent(kind.ToString()), "kind" },
            { new StringContent(execution), "execution" },
            { new StringContent(build.Commit), "commit" },
            { new StringContent($"scan-{kind}"), "jobName" },
            { new StringContent(JsonSerializer.Serialize(metadata ?? new Dictionary<string, string>())), "metadata" },
        };
        if (artifact is not null)
        {
            form.Add(new StringContent(artifact.Deployable), "deployable");
            form.Add(new StringContent(artifact.Digest), "digest");
        }

        var file = new ByteArrayContent(report);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "report", "report.json");
        return zone.PostAsync($"/api/builds/{build.Id}/evidence", form);
    }

    // Submits complete evidence. `vulnerabilities` go into the Trivy report of the API image.
    public async Task SubmitAllEvidenceAsync(OpenBuild build, IReadOnlyList<Vulnerability>? vulnerabilities = null)
    {
        foreach (var kind in CommitKinds)
        {
            await Expect(await SubmitAsync(Security, build, kind, Reports.For(kind)), 201);
        }

        foreach (var artifact in build.Artifacts)
        {
            await Expect(await SubmitAsync(Build, build, EvidenceKind.Sbom, Reports.For(EvidenceKind.Sbom, artifact.Repository, artifact.Digest), artifact), 201);
            var trivy = Reports.For(EvidenceKind.VulnerabilityScan, artifact.Repository, artifact.Digest, artifact.Deployable == "commerce-api" ? vulnerabilities : null);
            await Expect(await SubmitAsync(Security, build, EvidenceKind.VulnerabilityScan, trivy, artifact, metadata: TrivyDatabase()), 201);
            await Expect(await SubmitAsync(Security, build, EvidenceKind.SecondaryVulnerabilityScan, Reports.For(EvidenceKind.SecondaryVulnerabilityScan, artifact.Repository, artifact.Digest), artifact), 201);
        }
    }

    public Dictionary<string, string> TrivyDatabase() => new()
    {
        ["databaseVersion"] = "2",
        ["databaseUpdatedAt"] = factory.Clock.GetUtcNow().AddHours(-3).ToString("O"),
    };

    public async Task<JsonElement> EvaluateBuildAsync(OpenBuild build)
    {
        var response = await Security.PostAsJsonAsync($"/api/builds/{build.Id}/evaluation", new { runId = build.RunId });
        await Expect(response, 200);
        await Expect(await Security.PostAsJsonAsync($"/api/builds/{build.Id}/completion", new { runId = build.RunId, succeeded = true }), 204);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Opens a release for the build's commit and binds a trust-zone run to it.
    public async Task<(Guid Id, long RunId)> StartReleaseAsync(OpenBuild build)
    {
        var runId = NewRunId();
        await using var scope = factory.Services.CreateAsyncScope();
        var releases = scope.ServiceProvider.GetRequiredService<ReleaseService>();
        var tag = $"v1.{Interlocked.Increment(ref _nextRun)}.0";
        var release = await releases.RequestAsync("commerce", tag, build.Commit, "user:release-manager", CancellationToken.None);
        Assert.True(release.Succeeded, release.Error?.Message);
        Assert.True((await releases.AttachRunAsync(release.Value.Id, runId, "service:controlplane", CancellationToken.None)).Succeeded);
        return (release.Value.Id, runId);
    }

    public static async Task Expect(HttpResponseMessage response, int status)
    {
        if ((int)response.StatusCode != status)
        {
            Assert.Fail($"Expected {status} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    public static async Task<string?> ProblemCode(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
