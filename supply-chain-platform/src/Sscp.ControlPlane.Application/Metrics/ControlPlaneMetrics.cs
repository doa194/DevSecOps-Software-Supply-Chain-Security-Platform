// Supply-chain metrics emitted by the Control Plane (exported to Prometheus).
using System.Diagnostics.Metrics;
using Sscp.ControlPlane.Application.Releases;
using Sscp.ControlPlane.Domain.Builds;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;
using Sscp.ControlPlane.Domain.Policy;

namespace Sscp.ControlPlane.Application.Metrics;

public static class ControlPlaneMetrics
{
    public const string MeterName = "Sscp.ControlPlane";
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> EvidenceIngested =
        Meter.CreateCounter<long>("sscp_evidence_ingested", description: "Evidence records accepted, by kind and scanner execution status");

    public static readonly Counter<long> EvidenceRejected =
        Meter.CreateCounter<long>("sscp_evidence_rejected", description: "Evidence submissions refused, by reason");

    public static readonly Counter<long> ScannerFailures =
        Meter.CreateCounter<long>("sscp_scanner_failures", description: "Evidence reporting that a scanner did not complete, by kind");

    public static readonly Counter<long> Findings =
        Meter.CreateCounter<long>("sscp_findings", description: "Findings ingested, by kind and severity");

    public static readonly Counter<long> Decisions =
        Meter.CreateCounter<long>("sscp_trust_decisions", description: "Trust decisions, by outcome");

    public static readonly Counter<long> Signatures =
        Meter.CreateCounter<long>("sscp_signatures", description: "Artifacts signed");

    public static readonly Counter<long> Promotions =
        Meter.CreateCounter<long>("sscp_promotions", description: "Artifacts promoted to the trusted location");

    public static readonly Counter<long> BuildsCompleted =
        Meter.CreateCounter<long>("sscp_builds_completed", description: "Builds whose pipeline finished, by kind and status");

    // From the build's creation (webhook received) to its completion: queueing plus pipeline time.
    public static readonly Histogram<double> BuildDuration =
        Meter.CreateHistogram<double>("sscp_build_duration", unit: "s", description: "Time from build request to completion, by kind and status");

    public static readonly Counter<long> Deployments =
        Meter.CreateCounter<long>("sscp_deployments", description: "Deployments reported by Argo CD, by result (deployed, unmatched, mismatch)");

    public static readonly Counter<long> ExceptionEvents =
        Meter.CreateCounter<long>("sscp_exception_events", description: "Risk exception lifecycle events, by status");

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    // Trust decisions are counted per scope: a pull-request gate, a main build, a release.
    public static readonly string[] DecisionScopes = ["source", "build", "release"];

    // Prometheus measures "how many in the last hour" as the difference between samples. A
    // series that first appears with the value 1 has no earlier sample, so its first event
    // counts as zero: the first refused decision after a restart would raise no alert and
    // show nothing on the dashboards. Publishing every known label combination at 0 as soon
    // as the metrics pipeline runs avoids that. Rejection reasons are open-ended, so only
    // the fixed one is published here; the alert for them also covers new reasons.
    public static void PublishKnownSeries()
    {
        foreach (var outcome in Enum.GetValues<DecisionOutcome>())
        {
            foreach (var scope in DecisionScopes)
            {
                Decisions.Add(0, Tag("outcome", outcome.ToString()), Tag("scope", scope));
            }
        }

        foreach (var kind in Enum.GetValues<BuildKind>())
        {
            BuildsCompleted.Add(0, Tag("kind", kind.ToString()), Tag("status", "succeeded"));
            BuildsCompleted.Add(0, Tag("kind", kind.ToString()), Tag("status", "failed"));
        }

        foreach (var kind in Enum.GetValues<EvidenceKind>())
        {
            ScannerFailures.Add(0, Tag("kind", kind.ToString()));
            foreach (var execution in Enum.GetValues<ExecutionStatus>())
            {
                EvidenceIngested.Add(0, Tag("kind", kind.ToString()), Tag("execution", execution.ToString()));
            }

            foreach (var severity in Enum.GetValues<Severity>())
            {
                Findings.Add(0, Tag("kind", kind.ToString()), Tag("severity", severity.ToString()));
            }
        }

        foreach (var result in Enum.GetValues<DeploymentResult>())
        {
            Deployments.Add(0, Tag("result", result.ToString().ToLowerInvariant()));
        }

        foreach (var status in Enum.GetValues<ExceptionStatus>())
        {
            ExceptionEvents.Add(0, Tag("status", status.ToString()));
        }

        Signatures.Add(0);
        Promotions.Add(0);
        EvidenceRejected.Add(0, Tag("reason", "report-invalid"));
    }
}
