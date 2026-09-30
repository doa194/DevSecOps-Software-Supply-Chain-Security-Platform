// Runs the trust gate for a build's artifacts or for a release, and records the result.
//
// Before evaluating, every raw report the decision depends on is read back from the
// evidence store and hashed again. A report that no longer matches the hash recorded at
// ingestion makes the decision FAIL, so tampering with stored evidence cannot produce trust.
using System.Security.Cryptography;
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Policy;
using Sscp.ControlPlane.Domain.Records;

namespace Sscp.ControlPlane.Application.Trust;

public sealed record ArtifactDecision(Guid ArtifactId, string Deployable, string Digest, TrustDecisionRecord Decision);

public sealed class TrustService(IControlPlaneStore store, IEvidenceStore evidenceStore, IPolicyProvider policies, TimeProvider clock)
{
    // Evaluates every artifact of a main build (end of the main pipeline).
    public async Task<Outcome<IReadOnlyList<ArtifactDecision>>> EvaluateBuildAsync(Guid buildId, long? runId, Caller caller, CancellationToken cancellationToken)
    {
        var build = await store.BuildAsync(buildId, cancellationToken);
        if (build is null)
        {
            return DomainError.NotFound("build.unknown", "Build not found.");
        }

        if (!RunBinding.Allows(caller, runId, build.AcceptsRun))
        {
            return DomainError.Forbidden("build.run.not-dispatched", "Only the pipeline run dispatched for this build can request its evaluation.");
        }

        var artifacts = await store.ArtifactsForBuildAsync(buildId, cancellationToken);
        if (artifacts.Count == 0)
        {
            return DomainError.Conflict("build.artifacts.missing", "The build has no registered artifacts to evaluate.");
        }

        var decisions = await EvaluateArtifactsAsync(artifacts, null, caller.Identity, cancellationToken);
        await store.SaveChangesAsync(cancellationToken);
        return Outcome.Ok(decisions);
    }

    // Source-only gate used for pull requests: the commit-level scans must be complete and
    // clean. There are no artifacts yet, so image evidence is not required.
    public async Task<Outcome<Decision>> EvaluateSourceAsync(Guid buildId, long? runId, Caller caller, CancellationToken cancellationToken)
    {
        var build = await store.BuildAsync(buildId, cancellationToken);
        if (build is null)
        {
            return DomainError.NotFound("build.unknown", "Build not found.");
        }

        if (!RunBinding.Allows(caller, runId, build.AcceptsRun))
        {
            return DomainError.Forbidden("build.run.not-dispatched", "Only the pipeline run dispatched for this build can request its evaluation.");
        }

        // Pull requests are judged on source evidence only; GitOps pull requests on the
        // secret scan and the scan of their rendered manifests.
        var policy = policies.Current with
        {
            MandatoryCommitEvidence = build.Kind == BuildKind.DeploymentChange
                ? policies.Current.MandatoryDeploymentChangeEvidence
                : policies.Current.MandatoryPullRequestEvidence,
            MandatoryArtifactEvidence = [],
        };
        var evidence = await store.EvidenceForBuildAsync(buildId, cancellationToken);
        var subject = new ArtifactUnderEvaluation(Guid.Empty, build.Id, build.Application, "(source)", build.Commit, string.Empty);
        var decision = TrustEvaluator.Evaluate(policy, new EvaluationInput(subject, evidence, new Dictionary<string, DateTimeOffset>(),
            await store.ExceptionsAsync(build.Application, cancellationToken), await FindTamperedAsync(evidence, cancellationToken), clock.GetUtcNow()));

        store.Audit(caller.Identity, "source-gate.evaluated", "build", build.Id.ToString(), new { outcome = decision.Outcome.ToString(), policy = decision.PolicyVersion });
        await store.SaveChangesAsync(cancellationToken);
        ControlPlaneMetrics.Decisions.Add(1, ControlPlaneMetrics.Tag("outcome", decision.Outcome.ToString()), ControlPlaneMetrics.Tag("scope", "source"));
        return decision;
    }

    internal async Task<IReadOnlyList<ArtifactDecision>> EvaluateArtifactsAsync(IReadOnlyList<Artifact> artifacts, Guid? releaseId, string actor, CancellationToken cancellationToken)
    {
        var policy = policies.Current;
        var now = clock.GetUtcNow();
        var results = new List<ArtifactDecision>();

        foreach (var buildGroup in artifacts.GroupBy(a => a.BuildId))
        {
            var evidence = await store.EvidenceForBuildAsync(buildGroup.Key, cancellationToken);
            var tampered = await FindTamperedAsync(evidence, cancellationToken);
            var application = buildGroup.First().Application;
            var exceptions = await store.ExceptionsAsync(application, cancellationToken);
            var fingerprints = evidence.SelectMany(e => e.Findings).Select(f => f.Fingerprint).Distinct().ToList();
            var firstSeen = await store.FirstSeenAsync(application, fingerprints, cancellationToken);

            foreach (var artifact in buildGroup)
            {
                var subject = new ArtifactUnderEvaluation(artifact.Id, artifact.BuildId, artifact.Application, artifact.Deployable, artifact.Commit, artifact.Digest);
                var decision = TrustEvaluator.Evaluate(policy, new EvaluationInput(subject, evidence, firstSeen, exceptions, tampered, now));
                var record = TrustDecisionRecord.From(artifact.Id, releaseId, decision, actor, now);
                store.Add(record);
                artifact.ApplyDecision(record.Id, decision.Outcome, actor, now);

                store.Audit(actor, "decision.recorded", "artifact", artifact.Id.ToString(), new
                {
                    reference = artifact.CandidateReference,
                    outcome = decision.Outcome.ToString(),
                    policy = decision.PolicyVersion,
                    release = releaseId,
                    failed = decision.Results.Where(r => !r.Passed && r.Blocking).Select(r => r.Rule).ToList(),
                    exceptions = decision.AppliedExceptions,
                });
                foreach (var change in artifact.PendingChanges)
                {
                    store.Audit(change.Actor, "artifact.state-changed", "artifact", artifact.Id.ToString(), new { from = change.From.ToString(), to = change.To.ToString(), change.Reason });
                }

                artifact.ClearPendingChanges();
                ControlPlaneMetrics.Decisions.Add(1, ControlPlaneMetrics.Tag("outcome", decision.Outcome.ToString()), ControlPlaneMetrics.Tag("scope", releaseId is null ? "build" : "release"));
                results.Add(new ArtifactDecision(artifact.Id, artifact.Deployable, artifact.Digest, record));
            }
        }

        return results;
    }

    private async Task<IReadOnlySet<Guid>> FindTamperedAsync(IReadOnlyList<EvidenceRecord> evidence, CancellationToken cancellationToken)
    {
        var tampered = new HashSet<Guid>();
        foreach (var record in evidence)
        {
            var content = await evidenceStore.ReadAsync(record.Report.ObjectKey, cancellationToken);
            if (content is null || Convert.ToHexStringLower(SHA256.HashData(content)) != record.Report.Sha256)
            {
                tampered.Add(record.Id);
            }
        }

        return tampered;
    }
}
