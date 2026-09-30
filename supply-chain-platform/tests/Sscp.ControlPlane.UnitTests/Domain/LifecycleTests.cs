// Lifecycles of artifacts, releases, builds and risk exceptions, plus artifact identity and
// the audit chain. These state rules are what stop an unapproved artifact from being signed
// or promoted, and an exception from outliving its approval.
using Sscp.ControlPlane.Domain.Artifacts;
using Sscp.ControlPlane.Domain.Auditing;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Policy;
using Sscp.ControlPlane.Domain.Releases;
using static Sscp.ControlPlane.UnitTests.TestData;

namespace Sscp.ControlPlane.UnitTests.Domain;

public sealed class ArtifactReferenceTests
{
    [Fact]
    public void A_digest_reference_is_accepted() =>
        Assert.True(ArtifactReference.Parse($"harbor.sscp.test:8443/commerce-candidates/commerce-api@{ImageDigest}").Succeeded);

    [Theory]
    [InlineData("harbor.sscp.test/commerce-trusted/commerce-api:v1.0.0")]
    [InlineData("harbor.sscp.test/commerce-trusted/commerce-api:latest@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("harbor.sscp.test/commerce-trusted/commerce-api@sha256:AAAA")]
    [InlineData("harbor.sscp.test/commerce-trusted/commerce-api@md5:0123")]
    public void Tags_and_malformed_digests_are_not_artifact_identities(string reference) =>
        Assert.False(ArtifactReference.Parse(reference).Succeeded);
}

public sealed class ArtifactLifecycleTests
{
    private static Artifact NewArtifact() =>
        Artifact.Register(BuildId, "commerce", "commerce-api", Commit, ArtifactReference.Parse($"harbor.sscp.test/commerce-candidates/commerce-api@{ImageDigest}").Value, Now);

    [Fact]
    public void An_approved_artifact_goes_through_signing_promotion_and_deployment()
    {
        var artifact = NewArtifact();

        Assert.True(artifact.MarkEvidencePending("ci", Now).Succeeded);
        Assert.True(artifact.ApplyDecision(Guid.NewGuid(), DecisionOutcome.Pass, "ci", Now).Succeeded);
        Assert.True(artifact.MarkSigned("trust", Now).Succeeded);
        Assert.True(artifact.MarkPromoted("harbor.sscp.test/commerce-trusted/commerce-api", "trust", Now).Succeeded);
        Assert.True(artifact.MarkDeployed("argocd", Now).Succeeded);

        Assert.Equal(ArtifactState.Deployed, artifact.State);
        Assert.Equal(5, artifact.PendingChanges.Count);
    }

    [Fact]
    public void A_rejected_artifact_cannot_be_signed()
    {
        var artifact = NewArtifact();
        artifact.ApplyDecision(Guid.NewGuid(), DecisionOutcome.Fail, "ci", Now);

        var signed = artifact.MarkSigned("trust", Now);

        Assert.Equal("artifact.transition.invalid", signed.Error!.Code);
    }

    [Fact]
    public void An_unsigned_artifact_cannot_be_promoted()
    {
        var artifact = NewArtifact();
        artifact.ApplyDecision(Guid.NewGuid(), DecisionOutcome.Pass, "ci", Now);

        Assert.False(artifact.MarkPromoted("harbor.sscp.test/commerce-trusted/commerce-api", "trust", Now).Succeeded);
    }

    [Fact]
    public void Re_evaluation_before_signing_can_withdraw_approval()
    {
        var artifact = NewArtifact();
        artifact.ApplyDecision(Guid.NewGuid(), DecisionOutcome.PassWithException, "ci", Now);

        artifact.ApplyDecision(Guid.NewGuid(), DecisionOutcome.Fail, "trust", Now);

        Assert.Equal(ArtifactState.Rejected, artifact.State);
    }

    [Fact]
    public void A_new_decision_after_signing_is_recorded_but_does_not_rewind_the_lifecycle()
    {
        var artifact = NewArtifact();
        artifact.ApplyDecision(Guid.NewGuid(), DecisionOutcome.Pass, "ci", Now);
        artifact.MarkSigned("trust", Now);
        var laterDecision = Guid.NewGuid();

        artifact.ApplyDecision(laterDecision, DecisionOutcome.Fail, "trust", Now);

        Assert.Equal(ArtifactState.Signed, artifact.State);
        Assert.Equal(laterDecision, artifact.LatestDecisionId);
    }

    [Fact]
    public void A_later_release_of_the_same_build_signs_and_promotes_again_without_moving_back()
    {
        var artifact = NewArtifact();
        artifact.ApplyDecision(Guid.NewGuid(), DecisionOutcome.Pass, "ci", Now);
        artifact.MarkSigned("trust", Now);
        artifact.MarkPromoted("harbor.sscp.test/commerce-trusted/commerce-api", "trust", Now);
        artifact.MarkDeployed("argocd", Now);

        var signedAgain = artifact.MarkSigned("trust", Now);
        var promotedAgain = artifact.MarkPromoted("harbor.sscp.test/commerce-trusted/commerce-api", "trust", Now);

        Assert.True(signedAgain.Succeeded && promotedAgain.Succeeded);
        Assert.Equal(ArtifactState.Deployed, artifact.State);
    }
}

public sealed class ReleaseLifecycleTests
{
    private static Release NewRelease() => Release.Request("commerce", "v1.2.3", Commit, BuildId, [Guid.NewGuid()], "rita", Now).Value;

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2")]
    [InlineData("latest")]
    public void Release_tags_must_be_semantic_versions(string tag) =>
        Assert.False(Release.Request("commerce", tag, Commit, BuildId, [Guid.NewGuid()], "rita", Now).Succeeded);

    [Fact]
    public void A_release_without_artifacts_is_refused() =>
        Assert.Equal("release.artifacts.missing", Release.Request("commerce", "v1.0.0", Commit, BuildId, [], "rita", Now).Error!.Code);

    [Fact]
    public void A_rejected_release_may_not_sign()
    {
        var release = NewRelease();
        release.ApplyDecision(DecisionOutcome.Fail, "trust", Now);

        Assert.False(release.MaySign);
        Assert.False(release.MarkSigned("trust", Now).Succeeded);
    }

    [Fact]
    public void A_release_moves_to_deployed_in_order()
    {
        var release = NewRelease();

        release.ApplyDecision(DecisionOutcome.PassWithException, "trust", Now);
        release.MarkSigned("trust", Now);
        release.MarkPromoted("trust", Now);
        release.MarkGitOpsUpdated("abc123", "trust", Now);
        release.MarkDeployed("argocd", Now);

        Assert.Equal(ReleaseState.Deployed, release.State);
        Assert.Equal("abc123", release.GitOpsCommit);
    }

    [Fact]
    public void Promotion_cannot_skip_signing()
    {
        var release = NewRelease();
        release.ApplyDecision(DecisionOutcome.Pass, "trust", Now);

        Assert.False(release.MarkPromoted("trust", Now).Succeeded);
    }
}

public sealed class BuildTests
{
    [Fact]
    public void Main_builds_require_a_full_commit_and_the_main_branch()
    {
        Assert.False(Build.Request("commerce", "commerce/commerce-app", "abc123", "refs/heads/main", BuildKind.MainBranch, null, Now).Succeeded);
        Assert.False(Build.Request("commerce", "commerce/commerce-app", Commit, "refs/heads/feature", BuildKind.MainBranch, null, Now).Succeeded);
    }

    [Fact]
    public void Only_attached_runs_are_accepted_and_only_while_running()
    {
        var build = Build.Request("commerce", "commerce/commerce-app", Commit, "refs/heads/main", BuildKind.MainBranch, null, Now).Value;
        build.AttachRun(7, Now);

        Assert.True(build.AcceptsRun(7));
        Assert.False(build.AcceptsRun(8));

        build.Complete(true, null, Now);
        Assert.False(build.AcceptsRun(7));
        Assert.False(build.AttachRun(9, Now).Succeeded);
    }
}

public sealed class RiskExceptionTests
{
    private static Outcome<RiskException> Request(EvidenceKind kind = EvidenceKind.VulnerabilityScan, string justification = "Vendor fix scheduled; component not reachable.", int days = 30) =>
        RiskException.Request("commerce", null, "CVE-1|pkg", kind, justification, null, "owner-1", Now.AddDays(days), Exceptions, Now);

    [Fact]
    public void Secrets_can_never_be_accepted_as_risk() =>
        Assert.Equal("exception.kind.not-allowed", Request(EvidenceKind.SecretScan).Error!.Code);

    [Fact]
    public void A_meaningful_justification_is_required() =>
        Assert.Equal("exception.justification.too-short", Request(justification: "because").Error!.Code);

    [Fact]
    public void Exceptions_cannot_be_permanent() =>
        Assert.Equal("exception.expiry.too-long", Request(days: 91).Error!.Code);

    [Fact]
    public void The_owner_cannot_approve_their_own_exception()
    {
        var exception = Request().Value;

        Assert.Equal(ErrorKind.Forbidden, exception.Approve("owner-1", null, Now).Error!.Kind);
        Assert.False(exception.IsValidAt(Now));
    }

    [Fact]
    public void An_approved_exception_is_valid_until_it_expires()
    {
        var exception = Request(days: 10).Value;
        exception.Approve("approver-1", "ok", Now);

        Assert.True(exception.IsValidAt(Now.AddDays(9)));
        Assert.False(exception.IsValidAt(Now.AddDays(10)));
    }

    [Fact]
    public void Expiry_is_recorded_only_when_due()
    {
        var exception = Request(days: 10).Value;
        exception.Approve("approver-1", "ok", Now);

        Assert.False(exception.Expire(Now.AddDays(5)).Succeeded);
        Assert.True(exception.Expire(Now.AddDays(10)).Succeeded);
        Assert.Equal(ExceptionStatus.Expired, exception.Status);
    }

    [Fact]
    public void A_revoked_exception_stops_applying()
    {
        var exception = Request().Value;
        exception.Approve("approver-1", "ok", Now);

        exception.Revoke("approver-1", "fixed upstream", Now);

        Assert.False(exception.IsValidAt(Now));
    }

    [Fact]
    public void A_rejected_exception_cannot_be_approved_later()
    {
        var exception = Request().Value;
        exception.Reject("approver-1", "no", Now);

        Assert.False(exception.Approve("approver-2", null, Now).Succeeded);
    }
}

public sealed class AuditChainTests
{
    private static List<AuditEntry> Chain(int length)
    {
        var entries = new List<AuditEntry>();
        var previous = AuditChain.Genesis;
        for (var i = 1; i <= length; i++)
        {
            var at = AuditChain.ToStoragePrecision(Now.AddTicks(123457 * i));
            var hash = AuditChain.Compute(previous, at, "ci", "artifact.signed", "artifact", $"{i}", "{}", null);
            entries.Add(new AuditEntry { Sequence = i, OccurredAt = at, Actor = "ci", Action = "artifact.signed", SubjectType = "artifact", SubjectId = $"{i}", Details = "{}", PreviousHash = previous, Hash = hash });
            previous = hash;
        }

        return entries;
    }

    [Fact]
    public void An_untouched_chain_verifies() => Assert.True(AuditChain.Verify(Chain(4)).Intact);

    [Fact]
    public void A_removed_entry_is_detected()
    {
        var chain = Chain(4);
        chain.RemoveAt(2);

        Assert.Equal(4, AuditChain.Verify(chain).FirstBrokenSequence);
    }
}
