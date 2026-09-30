// Response shapes of the API. Domain objects are mapped explicitly so that internal
// details (pending change lists, concurrency versions) never leak into the contract.
using Sscp.ControlPlane.Application.Releases;
using Sscp.ControlPlane.Application.Trust;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Policy;
using Sscp.ControlPlane.Domain.Records;
using Sscp.ControlPlane.Domain.Releases;

namespace Sscp.ControlPlane.Api.Endpoints;

public static class Views
{
    public static object Artifact(Artifact a) => new
    {
        a.Id, a.BuildId, a.Application, a.Deployable, a.Commit, reference = a.CandidateReference, a.Digest,
        state = a.State.ToString(), a.TrustedRepository, a.LatestDecisionId, a.RegisteredAt, a.UpdatedAt,
    };

    public static object Build(Build b) => new
    {
        b.Id, b.Application, b.SourceRepository, b.Commit, b.Ref, kind = b.Kind.ToString(), b.PullRequest,
        status = b.Status.ToString(), b.FailureReason, pipelineRuns = b.PipelineRunIds, b.CreatedAt, b.UpdatedAt,
    };

    public static object Evidence(EvidenceRecord e) => new
    {
        e.Id, e.BuildId, kind = e.Kind.ToString(), e.Commit, e.Deployable, digest = e.ArtifactDigest, execution = e.Execution.ToString(),
        tool = e.Tool, report = new { key = e.Report.ObjectKey, sha256 = e.Report.Sha256, size = e.Report.SizeBytes },
        e.GatePassed, e.GateDetail,
        findings = e.SeverityCounts().Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key.ToString(), pair => pair.Value),
    };

    public static object Decision(TrustDecisionRecord d) => new
    {
        d.Id, d.ArtifactId, d.ReleaseId, outcome = OutcomeName(d.Outcome), d.PolicyVersion, d.EvaluatedBy, d.EvaluatedAt,
        blocking = d.Results.Where(r => !r.Passed && r.Blocking).ToList(),
        warnings = d.Results.Where(r => !r.Passed && !r.Blocking).ToList(),
        passed = d.Results.Where(r => r.Passed).Select(r => r.Rule).ToList(),
        d.AppliedExceptions, d.EvidenceUsed,
    };

    public static object ArtifactDecision(ArtifactDecision d) => new
    {
        d.ArtifactId, d.Deployable, d.Digest, decision = Decision(d.Decision),
    };

    public static object SourceDecision(Decision d) => new
    {
        outcome = OutcomeName(d.Outcome), d.PolicyVersion,
        blocking = d.Results.Where(r => !r.Passed && r.Blocking).ToList(),
        warnings = d.Results.Where(r => !r.Passed && !r.Blocking).ToList(),
        passed = d.Results.Where(r => r.Passed).Select(r => r.Rule).ToList(),
        d.AppliedExceptions,
    };

    public static object Release(Release r) => new
    {
        r.Id, r.Application, r.Tag, r.Commit, r.BuildId, r.RequestedBy, state = r.State.ToString(), r.PipelineRunId,
        r.GitOpsCommit, r.FailureReason, artifacts = r.ArtifactIds, r.RequestedAt, r.UpdatedAt,
    };

    public static object ReleaseDecision(ReleaseDecision d) => new
    {
        release = Release(d.Release), outcome = OutcomeName(d.Outcome), artifacts = d.Artifacts.Select(ArtifactDecision),
    };

    public static object Exception(RiskException e) => new
    {
        e.Id, e.Application, e.Deployable, finding = e.FindingFingerprint, kind = e.Kind.ToString(), e.Justification, e.CompensatingControls,
        e.Owner, e.Approver, status = e.Status.ToString(), e.RequestedAt, e.DecidedAt, e.ExpiresAt, e.DecisionNote,
    };

    public static string OutcomeName(DecisionOutcome outcome) => DecisionOutcomes.Name(outcome);
}
