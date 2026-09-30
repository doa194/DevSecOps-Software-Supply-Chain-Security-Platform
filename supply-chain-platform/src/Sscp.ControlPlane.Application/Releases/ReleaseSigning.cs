// Signing grants and attestation material for approved releases.
//
// The trust zone has no standing right to sign. For an approved release, and only for the
// pipeline run bound to it, the Control Plane asks Vault for a signing grant: a single-use,
// response-wrapped secret_id of the `trust-signer` role. The grant is useless outside the
// trust runner and expires within minutes. The Control Plane itself can mint grants but
// can never sign.
//
// The attestation material is what the trust zone signs next to each image: SLSA-style
// build provenance assembled from the Control Plane's records, the trust decision made
// for this release, and the SBOM collected for the digest.
using System.Security.Cryptography;
using System.Text.Json;
using Sscp.ControlPlane.Application.Orchestration;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Policy;

namespace Sscp.ControlPlane.Application.Releases;

public sealed record SigningGrant(string RoleId, string WrappedSecretId, string WrapAccessor, DateTimeOffset ExpiresAt);

public interface ISigningGrantIssuer
{
    Task<SigningGrant> IssueAsync(Guid releaseId, long runId, CancellationToken cancellationToken);
}

public sealed class SigningUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record AttestationMaterial(object Provenance, object TrustDecision, JsonElement Sbom);

public sealed class ReleaseSigning(
    IControlPlaneStore store,
    IEvidenceStore evidenceStore,
    IPipelinePlatform platform,
    ISigningGrantIssuer grants,
    TimeProvider clock)
{
    public const string ProvenancePredicateType = "https://slsa.dev/provenance/v1";
    public const string TrustDecisionPredicateType = "https://sscp.test/attestations/trust-decision/v1";
    public const string BuildType = "https://sscp.test/buildtypes/main-pipeline/v1";

    public async Task<Outcome<SigningGrant>> IssueGrantAsync(Guid releaseId, long runId, Caller caller, CancellationToken cancellationToken)
    {
        var release = await store.ReleaseAsync(releaseId, cancellationToken);
        if (caller.Zone != CallerZone.Trust)
        {
            return DomainError.Forbidden("release.zone", "Only the trust zone receives signing grants.");
        }

        if (release is null)
        {
            return DomainError.NotFound("release.unknown", "Release not found.");
        }

        if (!release.AcceptsRun(runId))
        {
            return DomainError.Forbidden("release.run.not-dispatched", "Signing grants are issued only to the pipeline run dispatched for this release.");
        }

        if (!release.MaySign)
        {
            return DomainError.Conflict("release.not-approved", $"Release is {release.State}; only approved releases may be signed.");
        }

        SigningGrant grant;
        try
        {
            grant = await grants.IssueAsync(releaseId, runId, cancellationToken);
        }
        catch (SigningUnavailableException error)
        {
            return DomainError.Unavailable("signing.unavailable", error.Message);
        }

        // The wrapping accessor identifies the grant in Vault's audit log without revealing it.
        store.Audit(caller.Identity, "signing.grant-issued", "release", release.Id.ToString(),
            new { runId, wrapAccessor = grant.WrapAccessor, grant.ExpiresAt, tag = release.Tag });
        await store.SaveChangesAsync(cancellationToken);
        return grant;
    }

    public async Task<Outcome<AttestationMaterial>> MaterialAsync(Guid releaseId, Guid artifactId, long runId, Caller caller, CancellationToken cancellationToken)
    {
        var release = await store.ReleaseAsync(releaseId, cancellationToken);
        if (caller.Zone != CallerZone.Trust || release is null || !release.AcceptsRun(runId))
        {
            return DomainError.Forbidden("release.run.not-dispatched", "Attestation material is available only to the release's trust-zone run.");
        }

        if (!release.ArtifactIds.Contains(artifactId) || await store.ArtifactAsync(artifactId, cancellationToken) is not { } artifact)
        {
            return DomainError.NotFound("release.artifact.unknown", "The artifact is not part of this release.");
        }

        var decision = (await store.DecisionsForArtifactAsync(artifactId, cancellationToken))
            .Where(d => d.ReleaseId == release.Id).OrderBy(d => d.EvaluatedAt).LastOrDefault();
        if (decision is null || decision.Outcome == DecisionOutcome.Fail)
        {
            return DomainError.Conflict("release.decision.missing", "There is no positive decision for this artifact in this release.");
        }

        var build = (await store.BuildAsync(artifact.BuildId, cancellationToken))!;
        var evidence = await store.EvidenceForBuildAsync(build.Id, cancellationToken);
        var sbomRecord = evidence.Where(e => e.Kind == EvidenceKind.Sbom && e.ArtifactDigest == artifact.Digest)
            .OrderBy(e => e.ReceivedAt).LastOrDefault();
        var sbomBytes = sbomRecord is null ? null : await evidenceStore.ReadAsync(sbomRecord.Report.ObjectKey, cancellationToken);
        if (sbomBytes is null || Convert.ToHexStringLower(SHA256.HashData(sbomBytes)) != sbomRecord!.Report.Sha256)
        {
            return DomainError.Conflict("evidence.integrity", "The stored SBOM is missing or no longer matches its recorded hash.");
        }

        var repository = platform.RepositoryUrl(build.SourceRepository);
        var provenance = new
        {
            buildDefinition = new
            {
                buildType = BuildType,
                externalParameters = new { repository, @ref = build.Ref, commit = build.Commit, deployable = artifact.Deployable, dockerfileTarget = artifact.Deployable },
                internalParameters = new { application = build.Application, buildId = build.Id, candidate = artifact.CandidateReference },
                resolvedDependencies = new[] { new { uri = $"git+{repository}@{build.Ref}", digest = new Dictionary<string, string> { ["gitCommit"] = build.Commit } } },
            },
            runDetails = new
            {
                builder = new { id = platform.WorkflowUrl(Workflows.Main) },
                metadata = new { invocationId = platform.RunUrl(build.PipelineRunIds[^1]), startedOn = build.CreatedAt, finishedOn = build.UpdatedAt },
            },
        };
        var used = evidence.Where(e => decision.EvidenceUsed.Contains(e.Id))
            .Select(e => new { kind = e.Kind.ToString(), tool = e.Tool.Name, version = e.Tool.Version, reportSha256 = e.Report.Sha256 });
        var trustDecision = new
        {
            decision = new { id = decision.Id, outcome = DecisionOutcomes.Name(decision.Outcome), decision.PolicyVersion, decision.EvaluatedAt, decision.EvaluatedBy },
            release = new { id = release.Id, release.Tag },
            artifact = new { application = artifact.Application, artifact.Deployable, artifact.Commit, artifact.Digest },
            appliedExceptions = decision.AppliedExceptions,
            evidence = used,
            attestedAt = clock.GetUtcNow(),
        };
        using var sbom = JsonDocument.Parse(sbomBytes);
        return new AttestationMaterial(provenance, trustDecision, sbom.RootElement.Clone());
    }
}
