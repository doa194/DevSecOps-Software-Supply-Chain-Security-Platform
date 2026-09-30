// The trust gate: a pure function from (artifact, evidence, exceptions, policy, time) to a
// decision with reasons. No I/O happens here, so every rule is exhaustively unit tested
// and the same inputs always produce the same decision.
//
// An artifact is trusted only when:
//   1. every mandatory scan exists for this exact commit and digest and ran to completion,
//   2. every raw report still matches the hash recorded when it was received,
//   3. vulnerability databases were fresh,
//   4. no secret was found (never acceptable as risk),
//   5. no blocking static-analysis, IaC, Dockerfile or DAST finding remains uncovered,
//   6. the quality gate and the security tests passed,
//   7. every vulnerability is within its SLA or covered by a valid exception.
// Outcome: FAIL if any blocking rule fails; PASS_WITH_EXCEPTION if exceptions were needed;
// otherwise PASS.
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;

namespace Sscp.ControlPlane.Domain.Policy;

public sealed record ArtifactUnderEvaluation(Guid Id, Guid BuildId, string Application, string Deployable, string Commit, string Digest);

public sealed record EvaluationInput(
    ArtifactUnderEvaluation Artifact,
    IReadOnlyList<EvidenceRecord> Evidence,
    IReadOnlyDictionary<string, DateTimeOffset> FindingFirstSeen,
    IReadOnlyList<RiskException> Exceptions,
    IReadOnlySet<Guid> TamperedEvidence,
    DateTimeOffset Now);

public sealed record RuleResult(string Rule, bool Passed, bool Blocking, string Message, IReadOnlyList<string> Findings)
{
    public static RuleResult Pass(string rule, string message) => new(rule, true, false, message, []);
    public static RuleResult Block(string rule, string message, IReadOnlyList<string>? findings = null) => new(rule, false, true, message, findings ?? []);
    public static RuleResult Warn(string rule, string message, IReadOnlyList<string>? findings = null) => new(rule, false, false, message, findings ?? []);
}

public sealed record Decision(
    DecisionOutcome Outcome,
    string PolicyVersion,
    IReadOnlyList<RuleResult> Results,
    IReadOnlyList<Guid> AppliedExceptions,
    IReadOnlyList<Guid> EvidenceUsed);

public static class TrustEvaluator
{
    public static Decision Evaluate(TrustPolicy policy, EvaluationInput input)
    {
        var results = new List<RuleResult>();
        var applied = new HashSet<Guid>();
        var selected = SelectEvidence(policy, input, results);

        foreach (var (kind, record) in selected)
        {
            results.Add(CheckIntegrity(kind, record, input.TamperedEvidence));
        }

        if (selected.TryGetValue(EvidenceKind.VulnerabilityScan, out var vulnerabilities))
        {
            results.Add(CheckDatabaseFreshness(policy, vulnerabilities, input.Now));
            results.AddRange(EvaluateVulnerabilities(policy, input, vulnerabilities, applied));
        }

        if (selected.TryGetValue(EvidenceKind.SecretScan, out var secrets))
        {
            results.Add(secrets.Findings.Count == 0
                ? RuleResult.Pass("secrets", "no secrets found")
                : RuleResult.Block("secrets", $"{secrets.Findings.Count} secret(s) found; secrets cannot be accepted as risk", secrets.Findings.Select(f => f.Fingerprint).ToList()));
        }

        Gate(EvidenceKind.StaticAnalysis, policy.StaticAnalysis, "static-analysis");
        Gate(EvidenceKind.InfrastructureScan, policy.Infrastructure, "infrastructure");
        // Rendered Kubernetes manifests of a GitOps change: the same bar as infrastructure code.
        Gate(EvidenceKind.DeploymentConfigScan, policy.Infrastructure, "deployment-config");
        Gate(EvidenceKind.DockerfileLint, policy.DockerfileLint, "dockerfile");
        Gate(EvidenceKind.DynamicScan, policy.DynamicScan, "dynamic-scan");
        PassFail(EvidenceKind.CodeQuality, "code-quality", "quality gate");
        PassFail(EvidenceKind.SecurityTests, "security-tests", "security tests");

        if (selected.TryGetValue(EvidenceKind.Sbom, out var sbom))
        {
            results.Add(sbom.GatePassed == true
                ? RuleResult.Pass("sbom", sbom.GateDetail ?? "SBOM describes this digest")
                : RuleResult.Block("sbom", sbom.GateDetail ?? "SBOM does not describe this digest or lists no components"));
        }

        var blocking = results.Any(r => r.Blocking && !r.Passed);
        var outcome = blocking ? DecisionOutcome.Fail : applied.Count > 0 ? DecisionOutcome.PassWithException : DecisionOutcome.Pass;
        return new Decision(outcome, policy.Version, results, applied.ToList(), selected.Values.Select(r => r.Id).ToList());

        void Gate(EvidenceKind kind, SeverityGate gate, string rule)
        {
            if (!selected.TryGetValue(kind, out var record))
            {
                return;
            }

            var blockingFindings = record.Findings.Where(f => f.Severity >= gate.BlockAtOrAbove).ToList();
            var uncovered = blockingFindings.Where(f => !TryCover(input, f, kind, gate.ExceptionsAllowed, applied)).Select(f => f.Fingerprint).ToList();
            results.Add(uncovered.Count == 0
                ? RuleResult.Pass(rule, blockingFindings.Count == 0 ? "no blocking findings" : $"{blockingFindings.Count} blocking finding(s) covered by exceptions")
                : RuleResult.Block(rule, $"{uncovered.Count} finding(s) at or above {gate.BlockAtOrAbove}", uncovered));
        }

        void PassFail(EvidenceKind kind, string rule, string label)
        {
            if (selected.TryGetValue(kind, out var record))
            {
                results.Add(record.GatePassed == true
                    ? RuleResult.Pass(rule, $"{label} passed")
                    : RuleResult.Block(rule, $"{label} failed: {record.GateDetail ?? "no result"}"));
            }
        }
    }

    // Picks the newest record per mandatory kind that belongs to this build, commit and
    // digest, and records a blocking result for anything missing, mismatched or not executed.
    private static Dictionary<EvidenceKind, EvidenceRecord> SelectEvidence(TrustPolicy policy, EvaluationInput input, List<RuleResult> results)
    {
        var artifact = input.Artifact;
        var selected = new Dictionary<EvidenceKind, EvidenceRecord>();
        var required = policy.MandatoryCommitEvidence.Select(k => (Kind: k, Subject: EvidenceSubject.Commit))
            .Concat(policy.MandatoryArtifactEvidence.Select(k => (Kind: k, Subject: EvidenceSubject.Artifact)));

        foreach (var (kind, subject) in required)
        {
            var candidates = input.Evidence.Where(e => e.Kind == kind && e.BuildId == artifact.BuildId).ToList();
            var bound = candidates.Where(e => string.Equals(e.Commit, artifact.Commit, StringComparison.Ordinal)
                && (subject == EvidenceSubject.Commit || string.Equals(e.ArtifactDigest, artifact.Digest, StringComparison.Ordinal))).ToList();
            var rule = $"evidence.{kind}";

            if (bound.Count == 0)
            {
                results.Add(candidates.Count == 0
                    ? RuleResult.Block(rule, $"mandatory {kind} evidence is missing")
                    : RuleResult.Block(rule, $"{kind} evidence exists but describes a different commit or digest"));
                continue;
            }

            var latest = bound.OrderByDescending(e => e.ReceivedAt).First();
            if (latest.Execution != ExecutionStatus.Completed)
            {
                // Fail closed: a scanner that did not run is not evidence of safety.
                results.Add(RuleResult.Block(rule, $"{kind} scanner did not complete: {latest.ExecutionError ?? "unknown error"}"));
                continue;
            }

            results.Add(RuleResult.Pass(rule, $"{latest.Tool.Name} {latest.Tool.Version} completed"));
            selected[kind] = latest;
        }

        return selected;
    }

    private static RuleResult CheckIntegrity(EvidenceKind kind, EvidenceRecord record, IReadOnlySet<Guid> tampered) =>
        tampered.Contains(record.Id)
            ? RuleResult.Block($"integrity.{kind}", "stored report no longer matches the hash recorded at ingestion")
            : RuleResult.Pass($"integrity.{kind}", "report hash verified");

    private static RuleResult CheckDatabaseFreshness(TrustPolicy policy, EvidenceRecord record, DateTimeOffset now)
    {
        var updated = record.Tool.DatabaseUpdatedAt;
        if (updated is null)
        {
            return RuleResult.Block("vulnerability-database", "scan does not state its vulnerability database age");
        }

        var age = now - updated.Value;
        return age <= policy.MaxVulnerabilityDatabaseAge
            ? RuleResult.Pass("vulnerability-database", $"database {record.Tool.DatabaseVersion} is {age.TotalHours:0}h old")
            : RuleResult.Block("vulnerability-database", $"database is {age.TotalDays:0.#} days old (limit {policy.MaxVulnerabilityDatabaseAge.TotalDays} days)");
    }

    // Applies the SLA table to each vulnerability: inside its remediation window a finding is
    // tracked; after the window its action applies (block, warn or track). Blocking findings
    // can be covered by a valid exception.
    private static IEnumerable<RuleResult> EvaluateVulnerabilities(TrustPolicy policy, EvaluationInput input, EvidenceRecord scan, HashSet<Guid> applied)
    {
        var breachedBlocking = new List<string>();
        var covered = new List<string>();
        var warnings = new List<string>();

        foreach (var finding in scan.Findings)
        {
            var sla = policy.SlaFor(finding.Severity, finding.FixAvailable);
            var firstSeen = input.FindingFirstSeen.GetValueOrDefault(finding.Fingerprint, input.Now);
            var breached = input.Now - firstSeen >= TimeSpan.FromDays(sla.RemediationDays);
            if (!breached)
            {
                continue;
            }

            switch (sla.AfterWindow)
            {
                case SlaAction.Block when TryCover(input, finding, EvidenceKind.VulnerabilityScan, true, applied):
                    covered.Add(finding.Fingerprint);
                    break;
                case SlaAction.Block:
                    breachedBlocking.Add(finding.Fingerprint);
                    break;
                case SlaAction.Warn:
                    warnings.Add(finding.Fingerprint);
                    break;
            }
        }

        yield return breachedBlocking.Count == 0
            ? RuleResult.Pass("vulnerabilities", covered.Count == 0 ? "all vulnerabilities within policy" : $"{covered.Count} vulnerability(ies) covered by exceptions")
            : RuleResult.Block("vulnerabilities", $"{breachedBlocking.Count} vulnerability(ies) past their remediation window", breachedBlocking);

        if (warnings.Count > 0)
        {
            yield return RuleResult.Warn("vulnerabilities.sla", $"{warnings.Count} vulnerability(ies) past their remediation window (tracked, not blocking)", warnings);
        }
    }

    private static bool TryCover(EvaluationInput input, Finding finding, EvidenceKind kind, bool exceptionsAllowed, HashSet<Guid> applied)
    {
        if (!exceptionsAllowed)
        {
            return false;
        }

        var exception = input.Exceptions.FirstOrDefault(e =>
            e.Kind == kind && e.IsValidAt(input.Now) && e.Covers(input.Artifact.Application, input.Artifact.Deployable, finding.Fingerprint));
        if (exception is null)
        {
            return false;
        }

        applied.Add(exception.Id);
        return true;
    }
}
