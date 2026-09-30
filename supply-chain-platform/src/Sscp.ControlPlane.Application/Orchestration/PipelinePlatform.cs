// Port to the CI system (Gitea Actions) and the source events the Control Plane reacts to.
namespace Sscp.ControlPlane.Application.Orchestration;

public enum CommitState
{
    Pending,
    Success,
    Failure,
    Error,
}

public sealed record DispatchedRun(long RunId, string? Url);

public sealed record PipelineRun(long Id, bool Completed, string? Conclusion, string? Url);

public interface IPipelinePlatform
{
    // Starts a platform-owned workflow and returns the id of the run it created.
    Task<DispatchedRun> DispatchAsync(string workflow, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken);

    Task SetCommitStatusAsync(string repository, string commit, string context, CommitState state, string description, string? targetUrl, CancellationToken cancellationToken);

    Task<PipelineRun?> GetRunAsync(long runId, CancellationToken cancellationToken);

    Task<string?> ResolveTagCommitAsync(string repository, string tag, CancellationToken cancellationToken);

    // Stable, human-readable identities recorded in provenance.
    string RepositoryUrl(string repository);
    string WorkflowUrl(string workflow);
    string RunUrl(long runId);
}

// What happened in an application repository, already verified and parsed from a webhook.
public abstract record SourceEvent(string Repository, string Actor);

public sealed record PushedToMain(string Repository, string Actor, string Commit) : SourceEvent(Repository, Actor);

public sealed record PullRequestUpdated(string Repository, string Actor, int Number, string HeadCommit) : SourceEvent(Repository, Actor);

public sealed record TagPushed(string Repository, string Actor, string Tag) : SourceEvent(Repository, Actor);

// Commit status contexts. `sscp/source-security` is a required check on pull requests.
public static class StatusContexts
{
    public const string SourceSecurity = "sscp/source-security";
    public const string TrustDecision = "sscp/trust-decision";
    public const string Release = "sscp/release";
    public const string DeploymentSecurity = "sscp/deployment-security";
}

public static class Workflows
{
    public const string PullRequest = "pr-pipeline.yaml";
    public const string Main = "main-pipeline.yaml";
    public const string Release = "release-pipeline.yaml";
    public const string Deployment = "deployment-pipeline.yaml";
}
