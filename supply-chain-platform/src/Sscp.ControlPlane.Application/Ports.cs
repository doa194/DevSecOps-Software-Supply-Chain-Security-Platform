// Ports: what the application layer needs from the outside world. The infrastructure
// project implements them (PostgreSQL, MinIO, YAML policy files); tests can replace them.
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Policy;
using Sscp.ControlPlane.Domain.Records;
using Sscp.ControlPlane.Domain.Releases;

namespace Sscp.ControlPlane.Application;

public interface IEvidenceStore
{
    // Stores the raw report write-once and returns its reference with the SHA-256 the
    // Control Plane computed itself.
    Task<RawReport> StoreAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken);
}

public interface IControlPlaneStore
{
    Task<Build?> BuildAsync(Guid id, CancellationToken cancellationToken);
    Task<Build?> LatestBuildForCommitAsync(string application, string commit, BuildKind kind, CancellationToken cancellationToken);
    Task<IReadOnlyList<Build>> ActiveBuildsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Release>> ActiveReleasesAsync(CancellationToken cancellationToken);
    Task<Artifact?> ArtifactAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Artifact>> ArtifactsForBuildAsync(Guid buildId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Artifact>> ArtifactsByDigestAsync(string digest, CancellationToken cancellationToken);
    Task<IReadOnlyList<EvidenceRecord>> EvidenceForBuildAsync(Guid buildId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, DateTimeOffset>> FirstSeenAsync(string application, IReadOnlyCollection<string> fingerprints, CancellationToken cancellationToken);
    Task<IReadOnlyList<RiskException>> ExceptionsAsync(string application, CancellationToken cancellationToken);
    Task<RiskException?> ExceptionAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<RiskException>> ExceptionsDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<Release?> ReleaseAsync(Guid id, CancellationToken cancellationToken);
    Task<Release?> ReleaseByTagAsync(string application, string tag, CancellationToken cancellationToken);
    Task<Release?> ReleaseByGitOpsCommitAsync(string application, string commit, CancellationToken cancellationToken);
    Task<Release?> LatestDeployedReleaseAsync(string application, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrustDecisionRecord>> DecisionsForArtifactAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SignatureRecord>> SignaturesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PromotionRecord>> PromotionsForArtifactAsync(Guid artifactId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeploymentRecord>> DeploymentsForReleaseAsync(Guid releaseId, CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;

    // Queues an audit entry; entries are chained and written in the same transaction as the
    // changes they describe when SaveChangesAsync runs.
    void Audit(string actor, string action, string subjectType, string subjectId, object details);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record RegisteredApplication(
    string Name,
    string SourceRepository,
    IReadOnlyList<string> Deployables,
    string CandidateRepositoryPrefix,
    string TrustedRepositoryPrefix,
    GitOpsTarget? GitOps = null);

// Where an application's desired cluster state lives: the GitOps repository and the
// Kustomize directory whose `images` pin the released digests.
public sealed record GitOpsTarget(string Repository, string Path, string Environment);

public interface IDesiredStateReader
{
    // The image references (repository@sha256:...) pinned by the target at one Git
    // revision, or null when the revision or file cannot be read.
    Task<IReadOnlyList<string>?> PinnedImagesAsync(GitOpsTarget target, string revision, CancellationToken cancellationToken);
}

public interface IApplicationCatalog
{
    RegisteredApplication? Find(string name);
    RegisteredApplication? FindBySourceRepository(string repository);
    RegisteredApplication? FindByGitOpsRepository(string repository);
}

public interface IPolicyProvider
{
    TrustPolicy Current { get; }

    // Checkov severities per check id (Checkov's free edition does not grade checks).
    IReadOnlyDictionary<string, Severity> InfrastructureSeverities { get; }
}
