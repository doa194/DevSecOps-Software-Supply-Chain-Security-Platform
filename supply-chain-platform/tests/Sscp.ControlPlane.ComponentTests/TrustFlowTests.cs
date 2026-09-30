// End-to-end trust decisions through the API: evidence in, PASS / FAIL /
// PASS_WITH_EXCEPTION out, and the release path that may only sign what passed.
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;
using Sscp.ControlPlane.ComponentTests.Infrastructure;

namespace Sscp.ControlPlane.ComponentTests;

public sealed class TrustFlowTests(ControlPlaneFactory factory)
{
    private readonly Pipeline _pipeline = new(factory);

    [Fact]
    public async Task Clean_evidence_passes_and_the_release_is_signed_promoted_and_traceable()
    {
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build);

        var decisions = await _pipeline.EvaluateBuildAsync(build);
        Assert.All(decisions.EnumerateArray(), d => Assert.Equal("PASS", d.GetProperty("decision").GetProperty("outcome").GetString()));

        var (releaseId, runId) = await _pipeline.StartReleaseAsync(build);
        var evaluation = await EvaluateReleaseAsync(releaseId, runId);
        Assert.Equal("PASS", evaluation.GetProperty("outcome").GetString());

        foreach (var artifact in build.Artifacts)
        {
            await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/signatures", new
            {
                runId, artifactId = artifact.Id, keyReference = "hashivault://cosign-commerce",
                signatureReference = $"{artifact.Repository}:{artifact.Digest.Replace(':', '-')}.sig",
            }), 204);
            await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/promotions", new
            {
                runId, artifactId = artifact.Id, trustedRepository = $"{Pipeline.Trusted}/{artifact.Deployable}",
            }), 204);
        }

        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/gitops", new { runId, commit = Pipeline.NewCommit() }), 204);

        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        var release = await viewer.GetFromJsonAsync<JsonElement>($"/api/releases/{releaseId}");
        Assert.Equal("GitOpsUpdated", release.GetProperty("state").GetString());

        var trace = (await viewer.GetFromJsonAsync<JsonElement>($"/api/artifacts/{build.Artifacts[0].Digest}/trace"))[0];
        Assert.Equal(build.Commit, trace.GetProperty("build").GetProperty("commit").GetString());
        Assert.Equal("Promoted", trace.GetProperty("artifact").GetProperty("state").GetString());
        Assert.Equal(10, trace.GetProperty("evidence").GetArrayLength());
        Assert.Single(trace.GetProperty("signatures").EnumerateArray());
        Assert.Single(trace.GetProperty("promotions").EnumerateArray());

        var audit = await viewer.GetFromJsonAsync<JsonElement>("/api/audit/verification");
        Assert.True(audit.GetProperty("intact").GetBoolean());
    }

    [Fact]
    public async Task A_later_release_of_the_same_build_progresses_only_through_its_own_signatures_and_promotions()
    {
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build);
        await _pipeline.EvaluateBuildAsync(build);
        var (first, firstRun) = await _pipeline.StartReleaseAsync(build);
        await EvaluateReleaseAsync(first, firstRun);
        foreach (var artifact in build.Artifacts)
        {
            await SignAndPromoteAsync(first, firstRun, artifact);
        }

        // A new tag on the same commit: the artifacts are already promoted by the first release.
        var (second, secondRun) = await _pipeline.StartReleaseAsync(build);
        await EvaluateReleaseAsync(second, secondRun);
        await SignAndPromoteAsync(second, secondRun, build.Artifacts[0]);

        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        Assert.Equal("Approved", (await viewer.GetFromJsonAsync<JsonElement>($"/api/releases/{second}")).GetProperty("state").GetString());
        foreach (var artifact in build.Artifacts.Skip(1))
        {
            await SignAndPromoteAsync(second, secondRun, artifact);
        }

        Assert.Equal("Promoted", (await viewer.GetFromJsonAsync<JsonElement>($"/api/releases/{second}")).GetProperty("state").GetString());
    }

    private async Task SignAndPromoteAsync(Guid releaseId, long runId, RegisteredArtifact artifact)
    {
        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/signatures", new
        {
            runId, artifactId = artifact.Id, keyReference = "hashivault://cosign-commerce",
            signatureReference = $"{Pipeline.Trusted}/{artifact.Deployable}@{Pipeline.NewDigest()}",
        }), 204);
        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/promotions", new
        {
            runId, artifactId = artifact.Id, trustedRepository = $"{Pipeline.Trusted}/{artifact.Deployable}",
        }), 204);
    }

    [Fact]
    public async Task Critical_fixable_vulnerability_fails_and_the_release_cannot_be_signed()
    {
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build, [new Vulnerability(UniqueCve(), "Critical")]);

        var decisions = await _pipeline.EvaluateBuildAsync(build);
        var api = decisions.EnumerateArray().Single(d => d.GetProperty("deployable").GetString() == "commerce-api");
        Assert.Equal("FAIL", api.GetProperty("decision").GetProperty("outcome").GetString());
        Assert.Contains(api.GetProperty("decision").GetProperty("blocking").EnumerateArray(), r => r.GetProperty("rule").GetString() == "vulnerabilities");

        var (releaseId, runId) = await _pipeline.StartReleaseAsync(build);
        Assert.Equal("FAIL", (await EvaluateReleaseAsync(releaseId, runId)).GetProperty("outcome").GetString());

        var signing = await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/signatures", new
        {
            runId, artifactId = build.Artifacts[0].Id, keyReference = "hashivault://cosign-commerce", signatureReference = "sig",
        });
        await Pipeline.Expect(signing, 409);
        Assert.Equal("release.not-approved", await Pipeline.ProblemCode(signing));
    }

    [Fact]
    public async Task Approved_exception_turns_the_failure_into_pass_with_exception_until_it_expires()
    {
        var cve = UniqueCve();
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build, [new Vulnerability(cve, "Critical")]);
        await ApproveExceptionAsync($"{cve}|System.Text.Json@8.0.0", expiresInDays: 10);

        var decisions = await _pipeline.EvaluateBuildAsync(build);
        var api = decisions.EnumerateArray().Single(d => d.GetProperty("deployable").GetString() == "commerce-api");
        Assert.Equal("PASS_WITH_EXCEPTION", api.GetProperty("decision").GetProperty("outcome").GetString());

        // Time passes between the main build and the release: the exception has expired.
        factory.Clock.Advance(TimeSpan.FromDays(11));
        var (releaseId, runId) = await _pipeline.StartReleaseAsync(build);
        var evaluation = await EvaluateReleaseAsync(releaseId, runId);

        Assert.Equal("FAIL", evaluation.GetProperty("outcome").GetString());
        Assert.Equal("Rejected", evaluation.GetProperty("release").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Evidence_changed_in_storage_after_ingestion_blocks_trust()
    {
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build);

        // Someone with storage administrator rights replaces the stored secret-scan report.
        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        var stored = await viewer.GetFromJsonAsync<JsonElement>($"/api/builds/{build.Id}");
        var key = stored.GetProperty("evidence").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "SecretScan").GetProperty("reportKey").GetString();
        await factory.StorageAdmin.PutObjectAsync(new PutObjectRequest { BucketName = ControlPlaneFactory.Bucket, Key = key, ContentBody = "[]\n" });

        var decisions = await _pipeline.EvaluateBuildAsync(build);

        Assert.All(decisions.EnumerateArray(), d =>
        {
            Assert.Equal("FAIL", d.GetProperty("decision").GetProperty("outcome").GetString());
            Assert.Contains(d.GetProperty("decision").GetProperty("blocking").EnumerateArray(), r => r.GetProperty("rule").GetString() == "integrity.SecretScan");
        });
    }

    private async Task<JsonElement> EvaluateReleaseAsync(Guid releaseId, long runId)
    {
        var response = await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/evaluation", new { runId });
        await Pipeline.Expect(response, 200);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task ApproveExceptionAsync(string finding, int expiresInDays)
    {
        using var owner = factory.ClientWith(TestTokens.Person("rita", "risk-owner", "platform-viewer"));
        var created = await owner.PostAsJsonAsync("/api/exceptions", new
        {
            application = "commerce", deployable = "commerce-api", finding, kind = "VulnerabilityScan",
            justification = "The vulnerable code path is unreachable; fix scheduled with the next base image.",
            compensatingControls = "Gateway rejects the affected content type.",
            expiresAt = factory.Clock.GetUtcNow().AddDays(expiresInDays),
        });
        await Pipeline.Expect(created, 201);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var approver = factory.ClientWith(TestTokens.Person("sean", "security-approver", "platform-viewer"));
        await Pipeline.Expect(await approver.PostAsJsonAsync($"/api/exceptions/{id}/approval", new { note = "accepted for one sprint" }), 204);
    }

    private static string UniqueCve() => $"CVE-2026-{Random.Shared.Next(10000, 99999)}";
}
