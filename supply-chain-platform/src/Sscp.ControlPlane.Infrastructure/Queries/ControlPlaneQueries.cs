// Read side of the Control Plane: traceability views, audit queries and state counts.
//
// Reads go straight to the database with no-tracking queries; they never change state, so
// they do not pass through the application services or the audit chain.
using Microsoft.EntityFrameworkCore;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Auditing;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Records;
using Sscp.ControlPlane.Domain.Releases;
using Sscp.ControlPlane.Infrastructure.Persistence;

namespace Sscp.ControlPlane.Infrastructure.Queries;

public sealed record EvidenceSummary(
    Guid Id, string Kind, string? Deployable, string? Digest, string Execution, string? ExecutionError, string Tool, string ToolVersion,
    string? DatabaseVersion, DateTimeOffset? DatabaseUpdatedAt, string ReportKey, string ReportSha256, long ReportSize,
    long PipelineRunId, string JobName, string SubmittedBy, DateTimeOffset ReceivedAt, bool? GatePassed, string? GateDetail,
    IReadOnlyDictionary<string, int> Findings);

public sealed record ArtifactTrace(
    Artifact Artifact,
    Build Build,
    IReadOnlyList<EvidenceSummary> Evidence,
    IReadOnlyList<TrustDecisionRecord> Decisions,
    IReadOnlyList<SignatureRecord> Signatures,
    IReadOnlyList<PromotionRecord> Promotions,
    IReadOnlyList<Release> Releases,
    IReadOnlyList<DeploymentRecord> Deployments);

public sealed record StateCounts(
    IReadOnlyDictionary<ArtifactState, int> Artifacts,
    IReadOnlyDictionary<ExceptionStatus, int> Exceptions,
    int ExceptionsExpiringWithinWeek);

public sealed class ControlPlaneQueries(ControlPlaneDbContext db, TimeProvider clock)
{
    public Task<Build?> BuildAsync(Guid id, CancellationToken cancellationToken) =>
        db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Build>> RecentBuildsAsync(string? application, int limit, CancellationToken cancellationToken) =>
        await db.Builds.AsNoTracking()
            .Where(b => application == null || b.Application == application)
            .OrderByDescending(b => b.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Artifact>> ArtifactsForBuildAsync(Guid buildId, CancellationToken cancellationToken) =>
        await db.Artifacts.AsNoTracking().Where(a => a.BuildId == buildId).OrderBy(a => a.Deployable).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EvidenceSummary>> EvidenceForBuildAsync(Guid buildId, CancellationToken cancellationToken)
    {
        var records = await db.Evidence.AsNoTracking().Include(e => e.Findings).Where(e => e.BuildId == buildId)
            .OrderBy(e => e.ReceivedAt).ToListAsync(cancellationToken);
        return records.Select(Summarise).ToList();
    }

    public async Task<EvidenceRecord?> EvidenceAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Evidence.AsNoTracking().Include(e => e.Findings).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    // Everything the platform knows about one image digest, from commit to cluster.
    public async Task<IReadOnlyList<ArtifactTrace>> TraceAsync(string digest, CancellationToken cancellationToken)
    {
        var traces = new List<ArtifactTrace>();
        var artifacts = await db.Artifacts.AsNoTracking().Where(a => a.Digest == digest).OrderBy(a => a.RegisteredAt).ToListAsync(cancellationToken);
        foreach (var artifact in artifacts)
        {
            var build = await db.Builds.AsNoTracking().FirstAsync(b => b.Id == artifact.BuildId, cancellationToken);
            var evidence = await db.Evidence.AsNoTracking().Include(e => e.Findings)
                .Where(e => e.BuildId == artifact.BuildId && (e.ArtifactDigest == null || e.ArtifactDigest == digest))
                .OrderBy(e => e.ReceivedAt).ToListAsync(cancellationToken);
            var releases = await db.Releases.AsNoTracking()
                .Where(r => EF.Property<List<Guid>>(r, "_artifactIds").Contains(artifact.Id)).ToListAsync(cancellationToken);
            var releaseIds = releases.Select(r => (Guid?)r.Id).ToList();
            traces.Add(new ArtifactTrace(
                artifact,
                build,
                evidence.Select(Summarise).ToList(),
                await db.Decisions.AsNoTracking().Where(d => d.ArtifactId == artifact.Id).OrderBy(d => d.EvaluatedAt).ToListAsync(cancellationToken),
                await db.Signatures.AsNoTracking().Where(s => s.ArtifactId == artifact.Id).ToListAsync(cancellationToken),
                await db.Promotions.AsNoTracking().Where(p => p.ArtifactId == artifact.Id).ToListAsync(cancellationToken),
                releases,
                await db.Deployments.AsNoTracking().Where(d => releaseIds.Contains(d.ReleaseId)).OrderBy(d => d.ObservedAt).ToListAsync(cancellationToken)));
        }

        return traces;
    }

    public Task<Release?> ReleaseAsync(Guid id, CancellationToken cancellationToken) =>
        db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<RiskException>> ExceptionsAsync(string? application, ExceptionStatus? status, CancellationToken cancellationToken) =>
        await db.Exceptions.AsNoTracking()
            .Where(e => (application == null || e.Application == application) && (status == null || e.Status == status))
            .OrderByDescending(e => e.RequestedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AuditEntry>> AuditAsync(string? subjectType, string? subjectId, int limit, CancellationToken cancellationToken) =>
        await db.AuditLog.AsNoTracking()
            .Where(a => (subjectType == null || a.SubjectType == subjectType) && (subjectId == null || a.SubjectId == subjectId))
            .OrderByDescending(a => a.Sequence).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(cancellationToken);

    // Recomputes the whole hash chain. Streams rows so memory use stays flat.
    public async Task<AuditChain.VerificationResult> VerifyAuditChainAsync(CancellationToken cancellationToken)
    {
        var entries = new List<AuditEntry>();
        await foreach (var entry in db.AuditLog.AsNoTracking().OrderBy(a => a.Sequence).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            entries.Add(entry);
        }

        return AuditChain.Verify(entries);
    }

    public async Task<StateCounts> CountsAsync(CancellationToken cancellationToken)
    {
        var artifacts = await db.Artifacts.AsNoTracking().GroupBy(a => a.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var exceptions = await db.Exceptions.AsNoTracking().GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var weekAhead = now.AddDays(7);
        var expiring = await db.Exceptions.AsNoTracking()
            .CountAsync(e => e.Status == ExceptionStatus.Approved && e.ExpiresAt > now && e.ExpiresAt <= weekAhead, cancellationToken);
        return new StateCounts(
            Enum.GetValues<ArtifactState>().ToDictionary(s => s, s => artifacts.FirstOrDefault(a => a.Key == s)?.Count ?? 0),
            Enum.GetValues<ExceptionStatus>().ToDictionary(s => s, s => exceptions.FirstOrDefault(e => e.Key == s)?.Count ?? 0),
            expiring);
    }

    private static EvidenceSummary Summarise(EvidenceRecord e) => new(
        e.Id, e.Kind.ToString(), e.Deployable, e.ArtifactDigest, e.Execution.ToString(), e.ExecutionError, e.Tool.Name, e.Tool.Version,
        e.Tool.DatabaseVersion, e.Tool.DatabaseUpdatedAt, e.Report.ObjectKey, e.Report.Sha256, e.Report.SizeBytes, e.PipelineRunId, e.JobName,
        e.SubmittedBy, e.ReceivedAt, e.GatePassed, e.GateDetail,
        e.SeverityCounts().Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
}
