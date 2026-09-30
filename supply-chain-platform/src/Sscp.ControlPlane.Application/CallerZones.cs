// Who is calling, and what each CI trust zone may submit.
//
// A zone may only submit the evidence it produces: the build zone cannot upload a "clean"
// vulnerability scan for its own image, and the security zone cannot register artifacts.
// Combined with runner scoping and run binding, this keeps each zone's authority narrow.
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Application;

public enum CallerZone
{
    None,
    Security,
    Build,
    Trust,
    Cluster,

    // The Control Plane itself: webhook orchestration and background jobs.
    ControlPlane,
}

public sealed record Caller(string Identity, CallerZone Zone);

public static class ZonePermissions
{
    private static readonly Dictionary<CallerZone, EvidenceKind[]> EvidenceByZone = new()
    {
        [CallerZone.Security] =
        [
            EvidenceKind.SecretScan, EvidenceKind.StaticAnalysis, EvidenceKind.CodeQuality, EvidenceKind.InfrastructureScan,
            EvidenceKind.DockerfileLint, EvidenceKind.VulnerabilityScan, EvidenceKind.SecondaryVulnerabilityScan,
            EvidenceKind.DynamicScan, EvidenceKind.SecurityTests, EvidenceKind.DeploymentConfigScan,
        ],
        [CallerZone.Build] = [EvidenceKind.Sbom],
        [CallerZone.Trust] = [EvidenceKind.Provenance],
        [CallerZone.Cluster] = [EvidenceKind.ClusterScan],
    };

    public static bool MaySubmit(CallerZone zone, EvidenceKind kind) =>
        EvidenceByZone.TryGetValue(zone, out var kinds) && kinds.Contains(kind);

    public static bool MayRegisterArtifacts(CallerZone zone) => zone == CallerZone.Build;
}

// Pipeline calls are accepted only from the run the Control Plane dispatched for the build
// or release. A valid zone token alone is not enough: it must also name the right run.
public static class RunBinding
{
    public static bool Allows(Caller caller, long? runId, Func<long, bool> accepts) =>
        caller.Zone == CallerZone.ControlPlane
        || (caller.Zone is CallerZone.Security or CallerZone.Build or CallerZone.Trust && runId is { } id && accepts(id));
}
