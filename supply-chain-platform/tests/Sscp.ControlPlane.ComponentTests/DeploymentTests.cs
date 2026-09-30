// Deployment reports from Argo CD: only the holder of the shared token may report, and a
// release counts as deployed only when its own GitOps revision runs, healthy, and that
// revision pins exactly the digests the Control Plane approved. Later revisions that keep
// those digests (reviewed configuration changes) stay linked to the release.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.ComponentTests.Infrastructure;

namespace Sscp.ControlPlane.ComponentTests;

public sealed class DeploymentTests(ControlPlaneFactory factory)
{
    private readonly Pipeline _pipeline = new(factory);

    [Fact]
    public async Task Healthy_sync_of_the_release_revision_with_its_digests_marks_the_release_deployed()
    {
        var (build, releaseId, gitOpsCommit) = await ReleaseInGitOpsAsync();
        factory.DesiredState.PinnedByRevision[gitOpsCommit] = TrustedImages(build);

        // Argo CD's own image summary is not needed: Git says what the revision runs.
        var response = await ReportAsync(gitOpsCommit, []);

        await Pipeline.Expect(response, 200);
        Assert.Equal("Deployed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        var release = await viewer.GetFromJsonAsync<JsonElement>($"/api/releases/{releaseId}");
        Assert.Equal("Deployed", release.GetProperty("state").GetString());
        var trace = (await viewer.GetFromJsonAsync<JsonElement>($"/api/artifacts/{build.Artifacts[0].Digest}/trace"))[0];
        Assert.Equal("Deployed", trace.GetProperty("artifact").GetProperty("state").GetString());
        Assert.Contains(trace.GetProperty("deployments").EnumerateArray(), d => d.GetProperty("gitOpsRevision").GetString() == gitOpsCommit);
        Assert.Equal(CommitState.Success, factory.Platform.LatestStatus(build.Commit, StatusContexts.Release).State);
    }

    [Fact]
    public async Task Report_without_the_shared_token_is_refused()
    {
        var (build, _, gitOpsCommit) = await ReleaseInGitOpsAsync();

        var anonymous = await ReportAsync(gitOpsCommit, TrustedImages(build), token: null);
        var wrong = await ReportAsync(gitOpsCommit, TrustedImages(build), token: "not-the-token");

        Assert.Equal(401, (int)anonymous.StatusCode);
        Assert.Equal(401, (int)wrong.StatusCode);
    }

    [Fact]
    public async Task A_revision_pinning_other_digests_than_the_release_approved_does_not_count_as_deployed()
    {
        var (build, releaseId, gitOpsCommit) = await ReleaseInGitOpsAsync();
        // For example the digest of an older signed release for one of the images.
        factory.DesiredState.PinnedByRevision[gitOpsCommit] =
            TrustedImages(build).Skip(1).Append($"{Pipeline.Trusted}/{build.Artifacts[0].Deployable}@{Pipeline.NewDigest()}").ToList();

        var response = await ReportAsync(gitOpsCommit, TrustedImages(build));

        Assert.Equal("Mismatch", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        Assert.Equal("GitOpsUpdated", (await viewer.GetFromJsonAsync<JsonElement>($"/api/releases/{releaseId}")).GetProperty("state").GetString());
        var audit = await viewer.GetStringAsync($"/api/audit?subjectType=release&subjectId={releaseId}");
        Assert.Contains("deployment.mismatch", audit);
    }

    [Fact]
    public async Task A_later_configuration_change_that_keeps_the_release_digests_stays_traceable_to_the_release()
    {
        var (build, _, gitOpsCommit) = await ReleaseInGitOpsAsync();
        factory.DesiredState.PinnedByRevision[gitOpsCommit] = TrustedImages(build);
        await Pipeline.Expect(await ReportAsync(gitOpsCommit, []), 200);
        var configurationChange = Pipeline.NewCommit();
        factory.DesiredState.PinnedByRevision[configurationChange] = TrustedImages(build);

        var response = await ReportAsync(configurationChange, []);

        Assert.Equal("AlreadyDeployed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        var trace = (await viewer.GetFromJsonAsync<JsonElement>($"/api/artifacts/{build.Artifacts[0].Digest}/trace"))[0];
        Assert.Contains(trace.GetProperty("deployments").EnumerateArray(), d => d.GetProperty("gitOpsRevision").GetString() == configurationChange);
    }

    [Fact]
    public async Task Revision_no_release_wrote_is_recorded_as_unmatched()
    {
        var response = await ReportAsync(Pipeline.NewCommit(), [$"{Pipeline.Trusted}/commerce-api@{Pipeline.NewDigest()}"]);

        Assert.Equal("Unmatched", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
    }

    private async Task<HttpResponseMessage> ReportAsync(string revision, IReadOnlyList<string> images, string? token = ControlPlaneFactory.ArgoCdToken)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/deployments/argocd")
        {
            Content = JsonContent.Create(new { application = "commerce", revision, syncStatus = "Synced", health = "Healthy", images }),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static List<string> TrustedImages(OpenBuild build) =>
        build.Artifacts.Select(a => $"{Pipeline.Trusted}/{a.Deployable}@{a.Digest}").ToList();

    // A release that passed, was signed and promoted, and whose digests were committed to GitOps.
    private async Task<(OpenBuild Build, Guid ReleaseId, string GitOpsCommit)> ReleaseInGitOpsAsync()
    {
        var build = await _pipeline.StartMainBuildAsync();
        await _pipeline.SubmitAllEvidenceAsync(build);
        await _pipeline.EvaluateBuildAsync(build);
        var (releaseId, runId) = await _pipeline.StartReleaseAsync(build);
        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/evaluation", new { runId }), 200);
        foreach (var artifact in build.Artifacts)
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

        var gitOpsCommit = Pipeline.NewCommit();
        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/releases/{releaseId}/gitops", new { runId, commit = gitOpsCommit }), 204);
        return (build, releaseId, gitOpsCommit);
    }
}
