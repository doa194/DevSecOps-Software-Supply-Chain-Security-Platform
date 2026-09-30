// IControlPlaneStore on PostgreSQL.
//
// Audit entries queued during a use case are written by SaveChangesAsync inside the same
// transaction as the state changes, under an advisory lock that serialises appends so
// every entry links to exactly one predecessor in the hash chain.
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sscp.ControlPlane.Application;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Auditing;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Records;
using Sscp.ControlPlane.Domain.Releases;

namespace Sscp.ControlPlane.Infrastructure.Persistence;

public sealed class EfControlPlaneStore(ControlPlaneDbContext db, TimeProvider clock) : IControlPlaneStore
{
    private const long AuditLockKey = 0x5353_4350_4155_4454; // "SSCPAUDT"
    private static readonly JsonSerializerOptions DetailsJson = new(JsonSerializerDefaults.Web);
    private readonly List<(string Actor, string Action, string SubjectType, string SubjectId, string Details)> _pendingAudit = [];

    public Task<Build?> BuildAsync(Guid id, CancellationToken cancellationToken) =>
        db.Builds.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public Task<Build?> LatestBuildForCommitAsync(string application, string commit, BuildKind kind, CancellationToken cancellationToken) =>
        db.Builds.Where(b => b.Application == application && b.Commit == commit && b.Kind == kind)
            .OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Build>> ActiveBuildsAsync(CancellationToken cancellationToken) =>
        await db.Builds.Where(b => b.Status == BuildStatus.Running).ToListAsync(cancellationToken);

    // Releases whose pipeline is still expected to act: bound to a run and not yet finished
    // with it (GitOpsUpdated waits for the cluster, not for the pipeline).
    public async Task<IReadOnlyList<Release>> ActiveReleasesAsync(CancellationToken cancellationToken) =>
        await db.Releases.Where(r => r.PipelineRunId != null && (r.State == ReleaseState.Requested || r.State == ReleaseState.Approved
            || r.State == ReleaseState.ApprovedWithException || r.State == ReleaseState.Signed || r.State == ReleaseState.Promoted))
            .ToListAsync(cancellationToken);

    public Task<Release?> ReleaseByGitOpsCommitAsync(string application, string commit, CancellationToken cancellationToken) =>
        db.Releases.FirstOrDefaultAsync(r => r.Application == application && r.GitOpsCommit == commit, cancellationToken);

    public Task<Release?> LatestDeployedReleaseAsync(string application, CancellationToken cancellationToken) =>
        db.Releases.Where(r => r.Application == application && r.State == ReleaseState.Deployed)
            .OrderByDescending(r => r.UpdatedAt).FirstOrDefaultAsync(cancellationToken);

    public Task<Artifact?> ArtifactAsync(Guid id, CancellationToken cancellationToken) =>
        db.Artifacts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Artifact>> ArtifactsForBuildAsync(Guid buildId, CancellationToken cancellationToken) =>
        await db.Artifacts.Where(a => a.BuildId == buildId).OrderBy(a => a.Deployable).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Artifact>> ArtifactsByDigestAsync(string digest, CancellationToken cancellationToken) =>
        await db.Artifacts.Where(a => a.Digest == digest).OrderBy(a => a.RegisteredAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EvidenceRecord>> EvidenceForBuildAsync(Guid buildId, CancellationToken cancellationToken) =>
        await db.Evidence.Include(e => e.Findings).Where(e => e.BuildId == buildId).OrderBy(e => e.ReceivedAt).ToListAsync(cancellationToken);

    // Age of a finding is measured from the first time this application's evidence contained
    // it, across all builds, so rebuilding does not reset a remediation deadline.
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> FirstSeenAsync(string application, IReadOnlyCollection<string> fingerprints, CancellationToken cancellationToken)
    {
        if (fingerprints.Count == 0)
        {
            return new Dictionary<string, DateTimeOffset>();
        }

        var rows = await (from finding in db.Findings
                          join evidence in db.Evidence on finding.EvidenceId equals evidence.Id
                          where evidence.Application == application && fingerprints.Contains(finding.Fingerprint)
                          group evidence.ReceivedAt by finding.Fingerprint into seen
                          select new { Fingerprint = seen.Key, FirstSeen = seen.Min() }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Fingerprint, r => r.FirstSeen, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<RiskException>> ExceptionsAsync(string application, CancellationToken cancellationToken) =>
        await db.Exceptions.Where(e => e.Application == application).ToListAsync(cancellationToken);

    public Task<RiskException?> ExceptionAsync(Guid id, CancellationToken cancellationToken) =>
        db.Exceptions.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<RiskException>> ExceptionsDueAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Exceptions.Where(e => (e.Status == ExceptionStatus.Approved || e.Status == ExceptionStatus.Requested) && e.ExpiresAt <= now)
            .ToListAsync(cancellationToken);

    public Task<Release?> ReleaseAsync(Guid id, CancellationToken cancellationToken) =>
        db.Releases.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public Task<Release?> ReleaseByTagAsync(string application, string tag, CancellationToken cancellationToken) =>
        db.Releases.FirstOrDefaultAsync(r => r.Application == application && r.Tag == tag, cancellationToken);

    public async Task<IReadOnlyList<TrustDecisionRecord>> DecisionsForArtifactAsync(Guid artifactId, CancellationToken cancellationToken) =>
        await db.Decisions.Where(d => d.ArtifactId == artifactId).OrderBy(d => d.EvaluatedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SignatureRecord>> SignaturesForArtifactAsync(Guid artifactId, CancellationToken cancellationToken) =>
        await db.Signatures.Where(s => s.ArtifactId == artifactId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PromotionRecord>> PromotionsForArtifactAsync(Guid artifactId, CancellationToken cancellationToken) =>
        await db.Promotions.Where(p => p.ArtifactId == artifactId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DeploymentRecord>> DeploymentsForReleaseAsync(Guid releaseId, CancellationToken cancellationToken) =>
        await db.Deployments.Where(d => d.ReleaseId == releaseId).OrderBy(d => d.ObservedAt).ToListAsync(cancellationToken);

    public void Add<TEntity>(TEntity entity) where TEntity : class => db.Add(entity);

    public void Audit(string actor, string action, string subjectType, string subjectId, object details) =>
        _pendingAudit.Add((actor, action, subjectType, subjectId, JsonSerializer.Serialize(details, DetailsJson)));

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (_pendingAudit.Count > 0)
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AuditLockKey})", cancellationToken);
            var previous = await db.AuditLog.OrderByDescending(a => a.Sequence).Select(a => a.Hash).FirstOrDefaultAsync(cancellationToken) ?? AuditChain.Genesis;
            var correlation = System.Diagnostics.Activity.Current?.TraceId.ToString();
            foreach (var (actor, action, subjectType, subjectId, details) in _pendingAudit)
            {
                var at = AuditChain.ToStoragePrecision(clock.GetUtcNow());
                var hash = AuditChain.Compute(previous, at, actor, action, subjectType, subjectId, details, correlation);
                db.AuditLog.Add(new AuditEntry
                {
                    OccurredAt = at, Actor = actor, Action = action, SubjectType = subjectType, SubjectId = subjectId,
                    Details = details, CorrelationId = correlation, PreviousHash = previous, Hash = hash,
                });
                previous = hash;
            }

            _pendingAudit.Clear();
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
