// Stands in for Gitea Actions: records dispatches and commit statuses and lets a test
// decide how a run ended. Gitea itself is exercised by the operational `ci` suite.
using System.Collections.Concurrent;
using Sscp.ControlPlane.Application.Orchestration;

namespace Sscp.ControlPlane.ComponentTests.Infrastructure;

public sealed record Dispatch(string Workflow, IReadOnlyDictionary<string, string> Inputs, long RunId);

public sealed record Status(string Repository, string Commit, string Context, CommitState State, string Description);

public sealed class RecordingPipelinePlatform : IPipelinePlatform
{
    private long _nextRun = 900_000;

    public ConcurrentQueue<Dispatch> Dispatches { get; } = new();
    public ConcurrentQueue<Status> Statuses { get; } = new();
    public ConcurrentDictionary<long, PipelineRun> EndedRuns { get; } = new();
    public ConcurrentDictionary<string, string> Tags { get; } = new();

    public Task<DispatchedRun> DispatchAsync(string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken)
    {
        var runId = Interlocked.Increment(ref _nextRun);
        Dispatches.Enqueue(new Dispatch(workflow, inputs, runId));
        return Task.FromResult(new DispatchedRun(runId, $"https://gitea.test/runs/{runId}"));
    }

    public Task SetCommitStatusAsync(string repository, string commit, string context, CommitState state, string description, string? targetUrl, CancellationToken cancellationToken)
    {
        Statuses.Enqueue(new Status(repository, commit, context, state, description));
        return Task.CompletedTask;
    }

    public Task<PipelineRun?> GetRunAsync(long runId, CancellationToken cancellationToken) =>
        Task.FromResult(EndedRuns.TryGetValue(runId, out var run) ? run : null);

    public Task<string?> ResolveTagCommitAsync(string repository, string tag, CancellationToken cancellationToken) =>
        Task.FromResult(Tags.TryGetValue(tag, out var commit) ? commit : null);

    public string RepositoryUrl(string repository) => $"https://gitea.test/{repository}";

    public string WorkflowUrl(string workflow) => $"https://gitea.test/platform/supply-chain-platform/.gitea/workflows/{workflow}@refs/heads/main";

    public string RunUrl(long runId) => $"https://gitea.test/platform/supply-chain-platform/actions/runs/{runId}";

    public Dispatch DispatchFor(string commit) => Dispatches.Single(d => d.Inputs.GetValueOrDefault("commit") == commit);

    public Status LatestStatus(string commit, string context) => Statuses.Last(s => s.Commit == commit && s.Context == context);
}
