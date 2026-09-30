// Source events in, pipelines and commit statuses out: the Control Plane starts the
// platform pipelines only for authentic webhooks, binds each to the run it started, and
// reports its own decisions as the statuses developers see.
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.ComponentTests.Infrastructure;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.ComponentTests;

public sealed class OrchestrationTests(ControlPlaneFactory factory)
{
    private const string Repository = "commerce/commerce-app";
    private const string GitOpsRepository = "platform/commerce-gitops";
    private readonly Pipeline _pipeline = new(factory);

    [Fact]
    public async Task Webhook_with_a_wrong_signature_starts_nothing()
    {
        var commit = Pipeline.NewCommit();
        var body = PullRequestPayload(commit, 7);

        var response = await DeliverAsync("pull_request", body, signature: Sign(body, "not-the-secret"));

        await Pipeline.Expect(response, 401);
        Assert.DoesNotContain(factory.Platform.Dispatches, d => d.Inputs.GetValueOrDefault("commit") == commit);
    }

    [Fact]
    public async Task Pull_request_starts_the_platform_source_pipeline_once_with_a_pending_required_status()
    {
        var commit = Pipeline.NewCommit();
        var body = PullRequestPayload(commit, 8);

        await Pipeline.Expect(await DeliverAsync("pull_request", body), 202);
        await Pipeline.Expect(await DeliverAsync("pull_request", body), 202); // re-delivery

        var dispatch = factory.Platform.DispatchFor(commit);
        Assert.Equal(Workflows.PullRequest, dispatch.Workflow);
        Assert.Equal("8", dispatch.Inputs["pull_request"]);
        Assert.Equal(CommitState.Pending, factory.Platform.LatestStatus(commit, StatusContexts.SourceSecurity).State);
    }

    [Theory]
    [InlineData(false, CommitState.Success)]
    [InlineData(true, CommitState.Failure)]
    public async Task Source_gate_decision_becomes_the_required_commit_status(bool leakedSecret, CommitState expected)
    {
        var commit = Pipeline.NewCommit();
        await Pipeline.Expect(await DeliverAsync("pull_request", PullRequestPayload(commit, 9)), 202);
        var dispatch = factory.Platform.DispatchFor(commit);
        var build = new OpenBuild(Guid.Parse(dispatch.Inputs["build_id"]), commit, dispatch.RunId, []);

        foreach (var kind in new[] { EvidenceKind.SecretScan, EvidenceKind.StaticAnalysis, EvidenceKind.InfrastructureScan, EvidenceKind.DockerfileLint })
        {
            var report = kind == EvidenceKind.SecretScan && leakedSecret ? Reports.Leak() : Reports.For(kind);
            await Pipeline.Expect(await Pipeline.SubmitAsync(_pipeline.Security, build, kind, report), 201);
        }

        await Pipeline.Expect(await _pipeline.Security.PostAsJsonAsync($"/api/builds/{build.Id}/source-evaluation", new { runId = build.RunId }), 200);

        Assert.Equal(expected, factory.Platform.LatestStatus(commit, StatusContexts.SourceSecurity).State);
    }

    [Theory]
    [InlineData(false, CommitState.Success)]
    [InlineData(true, CommitState.Failure)]
    public async Task GitOps_pull_request_gets_the_deployment_gate_on_its_rendered_manifests(bool privilegedPod, CommitState expected)
    {
        var commit = Pipeline.NewCommit();
        await Pipeline.Expect(await DeliverAsync("pull_request", PullRequestPayload(commit, 21, GitOpsRepository)), 202);
        var dispatch = factory.Platform.DispatchFor(commit);
        Assert.Equal(Workflows.Deployment, dispatch.Workflow);
        Assert.Equal(GitOpsRepository, dispatch.Inputs["repository"]);
        var build = new OpenBuild(Guid.Parse(dispatch.Inputs["build_id"]), commit, dispatch.RunId, []);

        await Pipeline.Expect(await Pipeline.SubmitAsync(_pipeline.Security, build, EvidenceKind.SecretScan, Reports.For(EvidenceKind.SecretScan)), 201);
        await Pipeline.Expect(await Pipeline.SubmitAsync(_pipeline.Security, build, EvidenceKind.DeploymentConfigScan, RenderedManifestScan(privilegedPod)), 201);
        await Pipeline.Expect(await _pipeline.Security.PostAsJsonAsync($"/api/builds/{build.Id}/source-evaluation", new { runId = build.RunId }), 200);

        Assert.Equal(expected, factory.Platform.LatestStatus(commit, StatusContexts.DeploymentSecurity).State);
        Assert.Equal(GitOpsRepository, factory.Platform.LatestStatus(commit, StatusContexts.DeploymentSecurity).Repository);
    }

    // Checkov's kustomize report for an overlay; optionally with a privileged container.
    private static byte[] RenderedManifestScan(bool privilegedPod) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        check_type = "kustomize",
        results = new
        {
            passed_checks = Array.Empty<object>(),
            failed_checks = privilegedPod
                ? new object[] { new { check_id = "CKV_K8S_16", check_name = "Container should not be privileged", resource = "Deployment.commerce.commerce-api", file_path = "/overlays/local/kustomization.yaml" } }
                : [],
        },
        summary = new { passed = 900, failed = privilegedPod ? 1 : 0, checkov_version = "3.3.19" },
    });

    [Fact]
    public async Task Tag_on_a_commit_without_a_successful_main_build_is_refused_without_a_pipeline()
    {
        var commit = Pipeline.NewCommit();
        factory.Platform.Tags["v9.9.1"] = commit;
        var body = PushPayload("refs/tags/v9.9.1", commit);

        await Pipeline.Expect(await DeliverAsync("push", body), 202);

        Assert.DoesNotContain(factory.Platform.Dispatches, d => d.Inputs.GetValueOrDefault("commit") == commit);
        Assert.Equal(CommitState.Failure, factory.Platform.LatestStatus(commit, StatusContexts.Release).State);
    }

    [Fact]
    public async Task Build_whose_pipeline_run_ended_without_completing_it_is_failed()
    {
        var commit = Pipeline.NewCommit();
        await Pipeline.Expect(await DeliverAsync("push", PushPayload("refs/heads/main", commit)), 202);
        var dispatch = factory.Platform.DispatchFor(commit);
        factory.Platform.EndedRuns[dispatch.RunId] = new PipelineRun(dispatch.RunId, Completed: true, Conclusion: "cancelled", Url: null);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrchestrationService>().ReconcileRunsAsync(CancellationToken.None);
        }

        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        var build = await viewer.GetFromJsonAsync<JsonElement>($"/api/builds/{dispatch.Inputs["build_id"]}");
        Assert.Equal("Failed", build.GetProperty("build").GetProperty("status").GetString());
        Assert.Equal(CommitState.Failure, factory.Platform.LatestStatus(commit, StatusContexts.TrustDecision).State);
    }

    [Fact]
    public async Task Only_a_platform_admin_can_retry_a_finished_build_and_the_retry_is_a_new_dispatched_build()
    {
        var commit = Pipeline.NewCommit();
        await Pipeline.Expect(await DeliverAsync("pull_request", PullRequestPayload(commit, 11)), 202);
        var first = factory.Platform.DispatchFor(commit);
        using var admin = factory.ClientWith(TestTokens.Person("paula", "platform-admin", "platform-viewer"));
        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));

        await Pipeline.Expect(await admin.PostAsync($"/api/builds/{first.Inputs["build_id"]}/retry", null), 409); // still running
        factory.Platform.EndedRuns[first.RunId] = new PipelineRun(first.RunId, Completed: true, Conclusion: "failure", Url: null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrchestrationService>().ReconcileRunsAsync(CancellationToken.None);
        }

        await Pipeline.Expect(await viewer.PostAsync($"/api/builds/{first.Inputs["build_id"]}/retry", null), 403);
        await Pipeline.Expect(await admin.PostAsync($"/api/builds/{first.Inputs["build_id"]}/retry", null), 202);

        var dispatches = factory.Platform.Dispatches.Where(d => d.Inputs.GetValueOrDefault("commit") == commit).ToList();
        Assert.Equal(2, dispatches.Count);
        Assert.NotEqual(dispatches[0].Inputs["build_id"], dispatches[1].Inputs["build_id"]);
    }

    private async Task<HttpResponseMessage> DeliverAsync(string eventType, byte[] body, string? signature = null)
    {
        using var client = factory.ClientWith(null);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/gitea") { Content = content };
        request.Headers.Add("X-Gitea-Event", eventType);
        request.Headers.Add("X-Gitea-Signature", signature ?? Sign(body, ControlPlaneFactory.WebhookSecret));
        return await client.SendAsync(request);
    }

    private static string Sign(byte[] body, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static byte[] PullRequestPayload(string head, int number, string repository = Repository) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        action = "opened",
        number,
        pull_request = new { number, head = new { @ref = "feature/x", sha = head }, @base = new { @ref = "main", sha = Pipeline.NewCommit() } },
        repository = new { full_name = repository },
        sender = new { login = "alice" },
    });

    private static byte[] PushPayload(string gitRef, string after) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        @ref = gitRef,
        before = Pipeline.NewCommit(),
        after,
        repository = new { full_name = Repository },
        pusher = new { login = "rhea" },
    });
}
