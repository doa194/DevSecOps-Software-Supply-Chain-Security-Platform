// The trust gate decides whether an artifact may ever be signed. Each test pins down one
// condition under which it must answer PASS, FAIL or PASS_WITH_EXCEPTION.
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Policy;
using static Sscp.ControlPlane.UnitTests.TestData;

namespace Sscp.ControlPlane.UnitTests.Policy;

public sealed class TrustEvaluatorTests
{
    private static RuleResult Failed(Decision decision, string rule) =>
        decision.Results.Single(r => r.Rule == rule && !r.Passed);

    [Fact]
    public void Complete_clean_evidence_passes()
    {
        var decision = Evaluate(CleanEvidence());

        Assert.Equal(DecisionOutcome.Pass, decision.Outcome);
        Assert.Equal("test-policy", decision.PolicyVersion);
        Assert.Equal(10, decision.EvidenceUsed.Count);
    }

    [Theory]
    [InlineData(EvidenceKind.SecretScan)]
    [InlineData(EvidenceKind.StaticAnalysis)]
    [InlineData(EvidenceKind.CodeQuality)]
    [InlineData(EvidenceKind.InfrastructureScan)]
    [InlineData(EvidenceKind.DockerfileLint)]
    [InlineData(EvidenceKind.DynamicScan)]
    [InlineData(EvidenceKind.SecurityTests)]
    [InlineData(EvidenceKind.Sbom)]
    [InlineData(EvidenceKind.VulnerabilityScan)]
    [InlineData(EvidenceKind.SecondaryVulnerabilityScan)]
    public void Missing_mandatory_evidence_fails(EvidenceKind missing)
    {
        var evidence = CleanEvidence();
        evidence.RemoveAll(e => e.Kind == missing);

        var decision = Evaluate(evidence);

        Assert.Equal(DecisionOutcome.Fail, decision.Outcome);
        Assert.Contains("missing", Failed(decision, $"evidence.{missing}").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scanner_that_did_not_complete_fails_closed()
    {
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.StaticAnalysis, execution: ExecutionStatus.Failed));

        var decision = Evaluate(evidence);

        Assert.Equal(DecisionOutcome.Fail, decision.Outcome);
        Assert.Contains("did not complete", Failed(decision, "evidence.StaticAnalysis").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_for_another_commit_does_not_count()
    {
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.SecretScan, commit: "fedcba9876543210fedcba9876543210fedcba98"));

        var decision = Evaluate(evidence);

        Assert.Contains("different commit or digest", Failed(decision, "evidence.SecretScan").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_for_another_digest_does_not_count()
    {
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, digest: "sha256:" + new string('b', 64)));

        Assert.Equal(DecisionOutcome.Fail, Evaluate(evidence).Outcome);
    }

    [Fact]
    public void Evidence_from_another_build_does_not_count()
    {
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.Sbom, buildId: Guid.NewGuid()));

        Assert.Contains("missing", Failed(Evaluate(evidence), "evidence.Sbom").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_newest_record_of_a_kind_is_used()
    {
        var evidence = CleanEvidence();
        evidence.Add(Evidence(EvidenceKind.StaticAnalysis, execution: ExecutionStatus.Failed, receivedAt: Now.AddHours(-2)));

        Assert.Equal(DecisionOutcome.Pass, Evaluate(evidence).Outcome);
    }

    [Fact]
    public void A_report_that_changed_after_ingestion_fails()
    {
        var evidence = CleanEvidence();
        var tampered = evidence.Single(e => e.Kind == EvidenceKind.VulnerabilityScan).Id;

        var decision = Evaluate(evidence, tampered: new HashSet<Guid> { tampered });

        Assert.Equal(DecisionOutcome.Fail, decision.Outcome);
        Assert.Single(decision.Results, r => r.Rule == "integrity.VulnerabilityScan" && !r.Passed);
    }

    [Fact]
    public void A_stale_vulnerability_database_fails()
    {
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, databaseUpdated: Now.AddDays(-8)));

        Assert.Contains("days old", Failed(Evaluate(evidence), "vulnerability-database").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_fails_even_when_an_exception_exists_for_it()
    {
        var secret = Issue("aws-access-key", Severity.Critical);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.SecretScan, [secret]));
        var exception = ApprovedException(Issue("unrelated", Severity.High), EvidenceKind.StaticAnalysis);

        var decision = Evaluate(evidence, [exception]);

        Assert.Equal(DecisionOutcome.Fail, decision.Outcome);
        Assert.Equal([secret.Fingerprint], Failed(decision, "secrets").Findings);
    }

    [Fact]
    public void A_fixable_critical_vulnerability_blocks_immediately()
    {
        var critical = Vulnerability("CVE-2026-0001", Severity.Critical, fixAvailable: true);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, [critical]));

        var decision = Evaluate(evidence);

        Assert.Equal(DecisionOutcome.Fail, decision.Outcome);
        Assert.Equal([critical.Fingerprint], Failed(decision, "vulnerabilities").Findings);
    }

    [Fact]
    public void A_valid_exception_turns_a_blocking_vulnerability_into_pass_with_exception()
    {
        var critical = Vulnerability("CVE-2026-0001", Severity.Critical, fixAvailable: true);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, [critical]));
        var exception = ApprovedException(critical, EvidenceKind.VulnerabilityScan);

        var decision = Evaluate(evidence, [exception]);

        Assert.Equal(DecisionOutcome.PassWithException, decision.Outcome);
        Assert.Equal([exception.Id], decision.AppliedExceptions);
    }

    [Fact]
    public void An_expired_exception_no_longer_satisfies_the_policy()
    {
        var critical = Vulnerability("CVE-2026-0001", Severity.Critical, fixAvailable: true);
        // The scan's database is kept fresh relative to the later evaluation times, so only
        // the exception's expiry differs between the two evaluations.
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, [critical], databaseUpdated: Now.AddDays(9)));
        var exception = ApprovedException(critical, EvidenceKind.VulnerabilityScan, expires: Now.AddDays(10));

        var before = Evaluate(evidence, [exception], now: Now.AddDays(9));
        var after = Evaluate(evidence, [exception], now: Now.AddDays(10));

        Assert.Equal(DecisionOutcome.PassWithException, before.Outcome);
        Assert.Equal(DecisionOutcome.Fail, after.Outcome);
    }

    [Fact]
    public void An_exception_for_another_deployable_does_not_apply()
    {
        var critical = Vulnerability("CVE-2026-0001", Severity.Critical, fixAvailable: true);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, [critical]));
        var exception = ApprovedException(critical, EvidenceKind.VulnerabilityScan, deployable: "audit-worker");

        Assert.Equal(DecisionOutcome.Fail, Evaluate(evidence, [exception]).Outcome);
    }

    [Fact]
    public void A_fixable_high_vulnerability_is_tolerated_for_seven_days_then_blocks()
    {
        var high = Vulnerability("CVE-2026-0002", Severity.High, fixAvailable: true);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, [high]));

        var withinWindow = Evaluate(evidence, firstSeen: new Dictionary<string, DateTimeOffset> { [high.Fingerprint] = Now.AddDays(-6) });
        var pastWindow = Evaluate(evidence, firstSeen: new Dictionary<string, DateTimeOffset> { [high.Fingerprint] = Now.AddDays(-7) });

        Assert.Equal(DecisionOutcome.Pass, withinWindow.Outcome);
        Assert.Equal(DecisionOutcome.Fail, pastWindow.Outcome);
    }

    [Fact]
    public void An_unfixable_critical_vulnerability_gets_fourteen_days()
    {
        var critical = Vulnerability("CVE-2026-0003", Severity.Critical, fixAvailable: false);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, [critical]));

        Assert.Equal(DecisionOutcome.Pass, Evaluate(evidence, firstSeen: new Dictionary<string, DateTimeOffset> { [critical.Fingerprint] = Now.AddDays(-13) }).Outcome);
        Assert.Equal(DecisionOutcome.Fail, Evaluate(evidence, firstSeen: new Dictionary<string, DateTimeOffset> { [critical.Fingerprint] = Now.AddDays(-14) }).Outcome);
    }

    [Fact]
    public void Medium_vulnerabilities_past_their_window_are_reported_but_do_not_block()
    {
        var medium = Vulnerability("CVE-2026-0004", Severity.Medium, fixAvailable: true);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.VulnerabilityScan, [medium]));

        var decision = Evaluate(evidence, firstSeen: new Dictionary<string, DateTimeOffset> { [medium.Fingerprint] = Now.AddDays(-60) });

        Assert.Equal(DecisionOutcome.Pass, decision.Outcome);
        Assert.Contains(decision.Results, r => r.Rule == "vulnerabilities.sla" && !r.Passed && !r.Blocking);
    }

    [Fact]
    public void A_high_static_analysis_finding_blocks_unless_accepted()
    {
        var finding = Issue("csharp.sensitive-data-logging", Severity.High);
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.StaticAnalysis, [finding]));

        var blocked = Evaluate(evidence);
        var accepted = Evaluate(evidence, [ApprovedException(finding, EvidenceKind.StaticAnalysis)]);

        Assert.Equal(DecisionOutcome.Fail, blocked.Outcome);
        Assert.Equal(DecisionOutcome.PassWithException, accepted.Outcome);
    }

    [Fact]
    public void Findings_below_the_gate_do_not_block()
    {
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.InfrastructureScan, [Issue("CKV_K8S_1", Severity.Medium)]));

        Assert.Equal(DecisionOutcome.Pass, Evaluate(evidence).Outcome);
    }

    [Theory]
    [InlineData(EvidenceKind.CodeQuality, "code-quality")]
    [InlineData(EvidenceKind.SecurityTests, "security-tests")]
    [InlineData(EvidenceKind.Sbom, "sbom")]
    public void A_failed_gate_fails(EvidenceKind kind, string rule)
    {
        var evidence = Replace(CleanEvidence(), Evidence(kind, gatePassed: false));

        var decision = Evaluate(evidence);

        Assert.Equal(DecisionOutcome.Fail, decision.Outcome);
        Assert.Single(decision.Results, r => r.Rule == rule && !r.Passed);
    }

    [Fact]
    public void Blocking_dast_alerts_fail_the_artifact()
    {
        var evidence = Replace(CleanEvidence(), Evidence(EvidenceKind.DynamicScan, [Issue("zap-10020", Severity.High)]));

        Assert.Equal(DecisionOutcome.Fail, Evaluate(evidence).Outcome);
    }
}
