// PostgreSQL model of the Control Plane (database `controlplane`, schema `trust`).
// Domain objects keep private setters; EF Core maps their backing fields directly so the
// domain rules stay the only way to change state.
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Auditing;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Policy;
using Sscp.ControlPlane.Domain.Records;
using Sscp.ControlPlane.Domain.Releases;

namespace Sscp.ControlPlane.Infrastructure.Persistence;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    public const string Schema = "trust";

    public DbSet<Build> Builds => Set<Build>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<EvidenceRecord> Evidence => Set<EvidenceRecord>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<RiskException> Exceptions => Set<RiskException>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<TrustDecisionRecord> Decisions => Set<TrustDecisionRecord>();
    public DbSet<SignatureRecord> Signatures => Set<SignatureRecord>();
    public DbSet<PromotionRecord> Promotions => Set<PromotionRecord>();
    public DbSet<DeploymentRecord> Deployments => Set<DeploymentRecord>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureBuild(modelBuilder.Entity<Build>());
        ConfigureArtifact(modelBuilder.Entity<Artifact>());
        ConfigureEvidence(modelBuilder.Entity<EvidenceRecord>(), modelBuilder.Entity<Finding>());
        ConfigureException(modelBuilder.Entity<RiskException>());
        ConfigureRelease(modelBuilder.Entity<Release>());
        ConfigureRecords(modelBuilder);
        ConfigureAudit(modelBuilder.Entity<AuditEntry>());
    }

    private static void ConfigureBuild(EntityTypeBuilder<Build> build)
    {
        build.ToTable("builds");
        build.HasKey(b => b.Id);
        build.Property(b => b.Application).HasMaxLength(60);
        build.Property(b => b.SourceRepository).HasMaxLength(200);
        build.Property(b => b.Commit).HasMaxLength(40);
        build.Property(b => b.Ref).HasMaxLength(200);
        build.Property(b => b.Kind).HasConversion<string>().HasMaxLength(20);
        build.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
        build.Property(b => b.FailureReason).HasMaxLength(500);
        build.Property(b => b.Version).IsConcurrencyToken();
        build.Ignore(b => b.PipelineRunIds);
        build.Property<List<long>>("_runIds").HasColumnName("pipeline_run_ids");
        build.HasIndex(b => new { b.Application, b.Commit, b.Kind });
    }

    private static void ConfigureArtifact(EntityTypeBuilder<Artifact> artifact)
    {
        artifact.ToTable("artifacts");
        artifact.HasKey(a => a.Id);
        artifact.Property(a => a.Application).HasMaxLength(60);
        artifact.Property(a => a.Deployable).HasMaxLength(60);
        artifact.Property(a => a.Commit).HasMaxLength(40);
        artifact.Property(a => a.CandidateRepository).HasMaxLength(200);
        artifact.Property(a => a.TrustedRepository).HasMaxLength(200);
        artifact.Property(a => a.Digest).HasMaxLength(71);
        artifact.Property(a => a.State).HasConversion<string>().HasMaxLength(30);
        artifact.Property(a => a.Version).IsConcurrencyToken();
        artifact.Ignore(a => a.PendingChanges);
        artifact.Ignore(a => a.CandidateReference);
        artifact.HasIndex(a => a.Digest);
        artifact.HasIndex(a => new { a.BuildId, a.Deployable }).IsUnique();
    }

    private static void ConfigureEvidence(EntityTypeBuilder<EvidenceRecord> evidence, EntityTypeBuilder<Finding> finding)
    {
        evidence.ToTable("evidence");
        evidence.HasKey(e => e.Id);
        evidence.Property(e => e.Application).HasMaxLength(60);
        evidence.Property(e => e.Commit).HasMaxLength(40);
        evidence.Property(e => e.Kind).HasConversion<string>().HasMaxLength(40);
        evidence.Property(e => e.Deployable).HasMaxLength(60);
        evidence.Property(e => e.ArtifactDigest).HasMaxLength(71);
        evidence.Property(e => e.Execution).HasConversion<string>().HasMaxLength(20);
        evidence.Property(e => e.ExecutionError).HasMaxLength(1000);
        evidence.Property(e => e.SubmittedBy).HasMaxLength(100);
        evidence.Property(e => e.JobName).HasMaxLength(100);
        evidence.Property(e => e.GateDetail).HasMaxLength(2000);
        evidence.ComplexProperty(e => e.Tool, tool =>
        {
            tool.Property(t => t.Name).HasColumnName("tool_name").HasMaxLength(60);
            tool.Property(t => t.Version).HasColumnName("tool_version").HasMaxLength(60);
            tool.Property(t => t.DatabaseVersion).HasColumnName("tool_database_version").HasMaxLength(100);
            tool.Property(t => t.DatabaseUpdatedAt).HasColumnName("tool_database_updated_at");
        });
        evidence.ComplexProperty(e => e.Report, report =>
        {
            report.Property(r => r.ObjectKey).HasColumnName("report_object_key").HasMaxLength(300);
            report.Property(r => r.Sha256).HasColumnName("report_sha256").HasMaxLength(64);
            report.Property(r => r.SizeBytes).HasColumnName("report_size_bytes");
            report.Property(r => r.ContentType).HasColumnName("report_content_type").HasMaxLength(100);
        });
        evidence.HasMany(e => e.Findings).WithOne().HasForeignKey(f => f.EvidenceId);
        evidence.Navigation(e => e.Findings).HasField("_findings");
        evidence.HasIndex(e => e.BuildId);
        evidence.HasIndex(e => new { e.Application, e.Kind });

        finding.ToTable("findings");
        finding.HasKey(f => f.Id);
        finding.Property(f => f.Fingerprint).HasMaxLength(500);
        finding.Property(f => f.RuleId).HasMaxLength(200);
        finding.Property(f => f.Severity).HasConversion<string>().HasMaxLength(20);
        finding.Property(f => f.Title).HasColumnType("text");
        finding.Property(f => f.Location).HasMaxLength(500);
        finding.Property(f => f.Package).HasMaxLength(200);
        finding.Property(f => f.InstalledVersion).HasMaxLength(100);
        finding.Property(f => f.FixedVersion).HasMaxLength(200);
        finding.HasIndex(f => f.Fingerprint);
    }

    private static void ConfigureException(EntityTypeBuilder<RiskException> exception)
    {
        exception.ToTable("risk_exceptions");
        exception.HasKey(e => e.Id);
        exception.Property(e => e.Application).HasMaxLength(60);
        exception.Property(e => e.Deployable).HasMaxLength(60);
        exception.Property(e => e.FindingFingerprint).HasMaxLength(500);
        exception.Property(e => e.Kind).HasConversion<string>().HasMaxLength(40);
        exception.Property(e => e.Justification).HasMaxLength(2000);
        exception.Property(e => e.CompensatingControls).HasMaxLength(2000);
        exception.Property(e => e.Owner).HasMaxLength(100);
        exception.Property(e => e.Approver).HasMaxLength(100);
        exception.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        exception.Property(e => e.DecisionNote).HasMaxLength(1000);
        exception.Property(e => e.Version).IsConcurrencyToken();
        exception.Ignore(e => e.PendingChanges);
        exception.HasIndex(e => new { e.Application, e.Status });
    }

    private static void ConfigureRelease(EntityTypeBuilder<Release> release)
    {
        release.ToTable("releases");
        release.HasKey(r => r.Id);
        release.Property(r => r.Application).HasMaxLength(60);
        release.Property(r => r.Tag).HasMaxLength(40);
        release.Property(r => r.Commit).HasMaxLength(40);
        release.Property(r => r.RequestedBy).HasMaxLength(100);
        release.Property(r => r.State).HasConversion<string>().HasMaxLength(30);
        release.Property(r => r.GitOpsCommit).HasMaxLength(40);
        release.Property(r => r.FailureReason).HasMaxLength(1000);
        release.Property(r => r.Version).IsConcurrencyToken();
        release.Ignore(r => r.PendingChanges);
        release.Ignore(r => r.ArtifactIds);
        release.Property<List<Guid>>("_artifactIds").HasColumnName("artifact_ids");
        release.HasIndex(r => new { r.Application, r.Tag }).IsUnique();
    }

    private static void ConfigureRecords(ModelBuilder model)
    {
        var decision = model.Entity<TrustDecisionRecord>();
        decision.ToTable("trust_decisions");
        decision.HasKey(d => d.Id);
        decision.Property(d => d.Outcome).HasConversion<string>().HasMaxLength(30);
        decision.Property(d => d.PolicyVersion).HasMaxLength(80);
        decision.Property(d => d.EvaluatedBy).HasMaxLength(100);
        JsonList(decision.Property(d => d.Results));
        JsonList(decision.Property(d => d.AppliedExceptions));
        JsonList(decision.Property(d => d.EvidenceUsed));
        decision.HasIndex(d => d.ArtifactId);

        var signature = model.Entity<SignatureRecord>();
        signature.ToTable("signatures");
        signature.HasKey(s => s.Id);
        signature.Property(s => s.Digest).HasMaxLength(71);
        signature.Property(s => s.KeyReference).HasMaxLength(200);
        signature.Property(s => s.SignatureReference).HasMaxLength(300);
        JsonList(signature.Property(s => s.AttestationReferences));
        signature.HasIndex(s => s.ArtifactId);

        var promotion = model.Entity<PromotionRecord>();
        promotion.ToTable("promotions");
        promotion.HasKey(p => p.Id);
        promotion.Property(p => p.From).HasMaxLength(300);
        promotion.Property(p => p.To).HasMaxLength(300);
        promotion.HasIndex(p => p.ArtifactId);

        var deployment = model.Entity<DeploymentRecord>();
        deployment.ToTable("deployments");
        deployment.HasKey(d => d.Id);
        deployment.Property(d => d.Application).HasMaxLength(60);
        deployment.Property(d => d.Environment).HasMaxLength(40);
        deployment.Property(d => d.GitOpsRevision).HasMaxLength(40);
        deployment.Property(d => d.SyncStatus).HasMaxLength(30);
        deployment.Property(d => d.HealthStatus).HasMaxLength(30);
        JsonList(deployment.Property(d => d.Images));
        deployment.HasIndex(d => d.ReleaseId);
    }

    private static void ConfigureAudit(EntityTypeBuilder<AuditEntry> audit)
    {
        audit.ToTable("audit_log");
        audit.HasKey(a => a.Sequence);
        audit.Property(a => a.Sequence).UseIdentityAlwaysColumn();
        audit.Property(a => a.Actor).HasMaxLength(100);
        audit.Property(a => a.Action).HasMaxLength(60);
        audit.Property(a => a.SubjectType).HasMaxLength(30);
        audit.Property(a => a.SubjectId).HasMaxLength(100);
        // Stored as text, exactly as hashed (jsonb would re-format it and break the chain).
        audit.Property(a => a.Details).HasColumnType("text");
        audit.Property(a => a.CorrelationId).HasMaxLength(100);
        audit.Property(a => a.PreviousHash).HasMaxLength(64);
        audit.Property(a => a.Hash).HasMaxLength(64);
        audit.HasIndex(a => new { a.SubjectType, a.SubjectId });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Read-only lists stored as jsonb. The comparer compares contents, so EF notices changes.
    private static void JsonList<T>(PropertyBuilder<IReadOnlyList<T>> property) => property
        .HasColumnType("jsonb")
        .HasConversion(
            new ValueConverter<IReadOnlyList<T>, string>(
                value => JsonSerializer.Serialize(value, JsonOptions),
                text => JsonSerializer.Deserialize<List<T>>(text, JsonOptions)!),
            new ValueComparer<IReadOnlyList<T>>(
                (left, right) => left!.SequenceEqual(right!),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                value => value.ToList()));
}
