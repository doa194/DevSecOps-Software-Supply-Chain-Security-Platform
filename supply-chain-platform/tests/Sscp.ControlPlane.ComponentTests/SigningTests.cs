// Who may receive a signing grant, and what the trust zone signs next to each image.
//
// A grant lets the trust runner sign with the release key, so it is handed out only to the
// run bound to an approved release. The attestation material must describe the build the
// artifact actually came from and the decision made for this release.
using System.Net.Http.Json;
using System.Text.Json;
using Sscp.ControlPlane.ComponentTests.Infrastructure;

namespace Sscp.ControlPlane.ComponentTests;

public sealed class SigningTests(ControlPlaneFactory factory)
{
    private readonly Pipeline _pipeline = new(factory);

    [Fact]
    public async Task Approved_release_run_receives_a_grant_and_material_describing_its_build()
    {
        var (build, releaseId, runId) = await ApprovedReleaseAsync();

        var grant = await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/signing-grant", new { runId });
        await Pipeline.Expect(grant, 200);
        Assert.Contains("no-store", grant.Headers.CacheControl!.ToString());
        Assert.Contains(factory.Grants.Issued, g => g.Release == releaseId && g.Run == runId);

        var artifact = build.Artifacts[0];
        var material = await _pipeline.Trust.GetFromJsonAsync<JsonElement>($"/api/releases/{releaseId}/artifacts/{artifact.Id}/attestations?runId={runId}");
        var provenance = material.GetProperty("provenance");
        Assert.Equal("https://slsa.dev/provenance/v1", provenance.GetProperty("predicateType").GetString());
        var definition = provenance.GetProperty("predicate").GetProperty("buildDefinition");
        Assert.Equal(build.Commit, definition.GetProperty("externalParameters").GetProperty("commit").GetString());
        Assert.Equal(build.Commit, definition.GetProperty("resolvedDependencies")[0].GetProperty("digest").GetProperty("gitCommit").GetString());
        Assert.EndsWith("main-pipeline.yaml@refs/heads/main",
            provenance.GetProperty("predicate").GetProperty("runDetails").GetProperty("builder").GetProperty("id").GetString());

        var decision = material.GetProperty("trustDecision").GetProperty("predicate");
        Assert.Equal("PASS", decision.GetProperty("decision").GetProperty("outcome").GetString());
        Assert.Equal(artifact.Digest, decision.GetProperty("artifact").GetProperty("digest").GetString());
        Assert.Equal(releaseId, decision.GetProperty("release").GetProperty("id").GetGuid());
        Assert.Equal(artifact.Digest, material.GetProperty("sbom").GetProperty("predicate").GetProperty("metadata").GetProperty("component").GetProperty("version").GetString());

        // The audit log records the grant by its accessor, never the wrapping token itself.
        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        var audit = await viewer.GetStringAsync($"/api/audit?subjectType=release&subjectId={releaseId}");
        Assert.Contains("signing.grant-issued", audit);
        Assert.Contains($"accessor-{releaseId:N}", audit);
        Assert.DoesNotContain($"hvs.wrapped-{releaseId:N}", audit);
    }

    [Fact]
    public async Task Grants_are_refused_before_approval_and_to_any_other_run_or_zone()
    {
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build);
        await _pipeline.EvaluateBuildAsync(build);
        var (releaseId, runId) = await _pipeline.StartReleaseAsync(build);

        // Not yet evaluated for release: nothing may be signed.
        var early = await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/signing-grant", new { runId });
        Assert.Equal("release.not-approved", await Pipeline.ProblemCode(early));
        var material = await _pipeline.Trust.GetAsync($"/api/releases/{releaseId}/artifacts/{build.Artifacts[0].Id}/attestations?runId={runId}");
        Assert.Equal("release.decision.missing", await Pipeline.ProblemCode(material));

        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/evaluation", new { runId }), 200);

        var otherRun = await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/signing-grant", new { runId = Pipeline.NewRunId() });
        Assert.Equal(403, (int)otherRun.StatusCode);
        Assert.Equal("release.run.not-dispatched", await Pipeline.ProblemCode(otherRun));

        foreach (var zone in new[] { _pipeline.Build, _pipeline.Security })
        {
            var response = await zone.PostAsJsonAsync($"/api/releases/{releaseId}/signing-grant", new { runId });
            Assert.Equal(403, (int)response.StatusCode);
            Assert.Equal("release.zone", await Pipeline.ProblemCode(response));
        }

        Assert.DoesNotContain(factory.Grants.Issued, g => g.Release == releaseId);
    }

    [Fact]
    public async Task Vault_outage_is_reported_as_unavailable_and_leaves_the_release_approved()
    {
        var (_, releaseId, runId) = await ApprovedReleaseAsync();
        factory.Grants.Unreachable[releaseId] = true;

        var response = await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/signing-grant", new { runId });

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal("signing.unavailable", await Pipeline.ProblemCode(response));
        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        var release = await viewer.GetFromJsonAsync<JsonElement>($"/api/releases/{releaseId}");
        Assert.Equal("Approved", release.GetProperty("state").GetString());
    }

    private async Task<(OpenBuild Build, Guid ReleaseId, long RunId)> ApprovedReleaseAsync()
    {
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build);
        await _pipeline.EvaluateBuildAsync(build);
        var (releaseId, runId) = await _pipeline.StartReleaseAsync(build);
        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/evaluation", new { runId }), 200);
        return (build, releaseId, runId);
    }
}
