// Who may do what through the API: each CI zone only its own actions, only from the run
// the Control Plane dispatched, and people only through their platform roles.
using System.Net.Http.Json;
using Sscp.ControlPlane.ComponentTests.Infrastructure;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.ComponentTests;

public sealed class ZoneBoundaryTests(ControlPlaneFactory factory)
{
    private readonly Pipeline _pipeline = new(factory);

    [Fact]
    public async Task Build_zone_cannot_vouch_for_its_own_image_with_a_vulnerability_scan()
    {
        var build = await _pipeline.StartMainBuildAsync();
        var artifact = build.Artifacts[0];

        var response = await Pipeline.SubmitAsync(_pipeline.Build, build, EvidenceKind.VulnerabilityScan,
            Reports.For(EvidenceKind.VulnerabilityScan, artifact.Repository, artifact.Digest), artifact, metadata: _pipeline.TrivyDatabase());

        await Pipeline.Expect(response, 403);
        Assert.Equal("evidence.zone.not-allowed", await Pipeline.ProblemCode(response));
    }

    [Fact]
    public async Task Security_zone_cannot_register_artifacts()
    {
        var build = await _pipeline.StartMainBuildAsync();

        var response = await _pipeline.Security.PostAsJsonAsync($"/api/builds/{build.Id}/artifacts",
            new { runId = build.RunId, deployable = "commerce-api", repository = $"{Pipeline.Candidate}/commerce-api", digest = Pipeline.NewDigest() });

        await Pipeline.Expect(response, 403);
    }

    [Fact]
    public async Task Evidence_from_a_run_the_control_plane_did_not_dispatch_is_refused()
    {
        var build = await _pipeline.StartMainBuildAsync();

        var response = await Pipeline.SubmitAsync(_pipeline.Security, build, EvidenceKind.SecretScan, Reports.For(EvidenceKind.SecretScan), runId: Pipeline.NewRunId());

        await Pipeline.Expect(response, 403);
        Assert.Equal("build.run.not-dispatched", await Pipeline.ProblemCode(response));
    }

    [Fact]
    public async Task Vulnerability_report_for_a_different_digest_is_refused()
    {
        var build = await _pipeline.StartMainBuildAsync();
        var artifact = build.Artifacts[0];

        var response = await Pipeline.SubmitAsync(_pipeline.Security, build, EvidenceKind.VulnerabilityScan,
            Reports.For(EvidenceKind.VulnerabilityScan, artifact.Repository, Pipeline.NewDigest()), artifact, metadata: _pipeline.TrivyDatabase());

        await Pipeline.Expect(response, 400);
        Assert.Equal("evidence.report.invalid", await Pipeline.ProblemCode(response));
    }

    [Fact]
    public async Task A_person_holding_a_zone_role_is_still_not_a_pipeline()
    {
        var build = await _pipeline.StartMainBuildAsync();
        using var impostor = factory.ClientWith(TestTokens.Person("mallory", "ci-security-zone"));

        var response = await impostor.PostAsJsonAsync($"/api/builds/{build.Id}/evaluation", new { runId = build.RunId });

        await Pipeline.Expect(response, 403);
    }

    [Fact]
    public async Task Pipelines_and_viewers_cannot_decide_risk_exceptions()
    {
        using var owner = factory.ClientWith(TestTokens.Person("rita", "risk-owner", "platform-viewer"));
        var created = await owner.PostAsJsonAsync("/api/exceptions", new
        {
            application = "commerce", finding = "CVE-2026-0001|pkg:nuget/Example@1.0.0", kind = "VulnerabilityScan",
            justification = "Not reachable: the vulnerable parser is never called by our code.", expiresAt = factory.Clock.GetUtcNow().AddDays(30),
        });
        await Pipeline.Expect(created, 201);
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        using var viewer = factory.ClientWith(TestTokens.Person("victor", "platform-viewer"));
        await Pipeline.Expect(await viewer.PostAsJsonAsync($"/api/exceptions/{id}/approval", new { note = "ok" }), 403);
        await Pipeline.Expect(await _pipeline.Trust.PostAsJsonAsync($"/api/exceptions/{id}/approval", new { note = "ok" }), 403);
    }

    [Fact]
    public async Task Nobody_approves_their_own_risk_exception()
    {
        using var approver = factory.ClientWith(TestTokens.Person("sean", "risk-owner", "security-approver"));
        var created = await approver.PostAsJsonAsync("/api/exceptions", new
        {
            application = "commerce", finding = "CVE-2026-0002|pkg:nuget/Example@1.0.0", kind = "VulnerabilityScan",
            justification = "Upstream fix is not released; the endpoint is disabled.", expiresAt = factory.Clock.GetUtcNow().AddDays(14),
        });
        await Pipeline.Expect(created, 201);
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        var response = await approver.PostAsJsonAsync($"/api/exceptions/{id}/approval", new { note = "looks fine" });

        await Pipeline.Expect(response, 403);
        Assert.Equal("exception.approval.self", await Pipeline.ProblemCode(response));
    }
}
