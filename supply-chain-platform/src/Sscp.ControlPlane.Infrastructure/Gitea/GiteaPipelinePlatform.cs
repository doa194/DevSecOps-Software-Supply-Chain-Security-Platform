// Gitea Actions as the pipeline platform: workflow dispatch, commit statuses and run state.
//
// The Control Plane acts as the `sscp-controlplane` Gitea user, which may start workflows
// in the platform repository and set commit statuses, and nothing else.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sscp.ControlPlane.Application.Orchestration;

namespace Sscp.ControlPlane.Infrastructure.Gitea;

public sealed class GiteaOptions
{
    public const string SectionName = "Gitea";
    public string BaseUrl { get; set; } = "https://gitea.sscp.test:3000";
    public string Token { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string PlatformRepository { get; set; } = "platform/supply-chain-platform";
    public string WorkflowRef { get; set; } = "main";
    public string? CaCertificatePath { get; set; }
}

public sealed class GiteaPipelinePlatform(HttpClient http, GiteaOptions options) : IPipelinePlatform
{
    public async Task<DispatchedRun> DispatchAsync(string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken)
    {
        // return_run_details makes Gitea answer with the id of the run it created, so the
        // run can be bound to the build before any of its jobs start.
        var response = await SendAsync(HttpMethod.Post,
            $"api/v1/repos/{options.PlatformRepository}/actions/workflows/{workflow}/dispatches?return_run_details=true",
            new { @ref = options.WorkflowRef, inputs }, cancellationToken);
        var details = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!details.TryGetProperty("workflow_run_id", out var id))
        {
            throw new PipelineDispatchException($"Gitea did not return a run id for {workflow}.");
        }

        return new DispatchedRun(id.GetInt64(), details.TryGetProperty("html_url", out var url) ? url.GetString() : null);
    }

    public async Task SetCommitStatusAsync(string repository, string commit, string context, CommitState state, string description, string? targetUrl, CancellationToken cancellationToken)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"api/v1/repos/{repository}/statuses/{commit}", new
        {
            state = state.ToString().ToLowerInvariant(),
            context,
            description,
            target_url = targetUrl ?? string.Empty,
        }, cancellationToken);
    }

    public async Task<PipelineRun?> GetRunAsync(long runId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/repos/{options.PlatformRepository}/actions/runs/{runId}", null, cancellationToken, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var run = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var status = run.TryGetProperty("status", out var s) ? s.GetString() : null;
        var conclusion = run.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return new PipelineRun(runId, status == "completed", conclusion, run.TryGetProperty("html_url", out var u) ? u.GetString() : null);
    }

    public async Task<string?> ResolveTagCommitAsync(string repository, string tag, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/repos/{repository}/tags/{Uri.EscapeDataString(tag)}", null, cancellationToken, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("commit").GetProperty("sha").GetString();
    }

    public string RepositoryUrl(string repository) => $"{options.BaseUrl.TrimEnd('/')}/{repository}";

    public string WorkflowUrl(string workflow) =>
        $"{options.BaseUrl.TrimEnd('/')}/{options.PlatformRepository}/.gitea/workflows/{workflow}@refs/heads/{options.WorkflowRef}";

    public string RunUrl(long runId) => $"{options.BaseUrl.TrimEnd('/')}/{options.PlatformRepository}/actions/runs/{runId}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new PipelineDispatchException($"Gitea is unreachable: {error.Message}", error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PipelineDispatchException("Gitea did not answer in time.", error);
        }

        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound))
        {
            return response;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Dispose();
        throw new PipelineDispatchException($"Gitea answered {(int)response.StatusCode} for {method} {path}: {detail[..Math.Min(detail.Length, 300)]}");
    }
}
