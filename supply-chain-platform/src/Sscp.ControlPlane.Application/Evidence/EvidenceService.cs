// Evidence ingestion: the only way scanner results enter the trust model.
//
// A submission is accepted only if, in this order:
//   1. the calling CI zone is allowed to submit this kind of evidence,
//   2. the build exists and the submitting pipeline run is one the Control Plane dispatched for it,
//   3. the claimed commit is the build's commit and, for image evidence, the digest is an
//      artifact registered by this build,
//   4. the raw report parses and itself names that commit or digest.
// The raw report is then stored write-once with a hash the Control Plane computes, and the
// findings are extracted here rather than taken from the caller.
using System.Diagnostics.Metrics;
using Sscp.ControlPlane.Application.Evidence.Readers;
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;

namespace Sscp.ControlPlane.Application.Evidence;

public sealed record SubmitEvidenceCommand(
    Guid BuildId,
    long PipelineRunId,
    string JobName,
    EvidenceKind Kind,
    string Commit,
    string? Deployable,
    string? Digest,
    ExecutionStatus Execution,
    string? ExecutionError,
    IReadOnlyDictionary<string, string> Metadata,
    byte[] Report,
    string ContentType);

public sealed class EvidenceService(IControlPlaneStore store, IEvidenceStore evidenceStore, EvidenceReaders readers, TimeProvider clock)
{
    public async Task<Outcome<EvidenceRecord>> SubmitAsync(SubmitEvidenceCommand command, Caller caller, CancellationToken cancellationToken)
    {
        var rejection = await ValidateAsync(command, caller, cancellationToken);
        if (rejection is not null)
        {
            ControlPlaneMetrics.EvidenceRejected.Add(1, ControlPlaneMetrics.Tag("reason", rejection.Code));
            store.Audit(caller.Identity, "evidence.rejected", "build", command.BuildId.ToString(),
                new { kind = command.Kind.ToString(), reason = rejection.Code, runId = command.PipelineRunId });
            await store.SaveChangesAsync(cancellationToken);
            return rejection;
        }

        var build = (await store.BuildAsync(command.BuildId, cancellationToken))!;
        ParsedReport parsed;
        if (command.Execution == ExecutionStatus.Completed)
        {
            try
            {
                parsed = readers.For(command.Kind).Parse(command.Report, new ReportContext(command.Commit, command.Digest, null, command.Metadata));
            }
            catch (ReportRejectedException error)
            {
                ControlPlaneMetrics.EvidenceRejected.Add(1, ControlPlaneMetrics.Tag("reason", "report-invalid"));
                store.Audit(caller.Identity, "evidence.rejected", "build", build.Id.ToString(), new { kind = command.Kind.ToString(), reason = error.Message });
                await store.SaveChangesAsync(cancellationToken);
                return DomainError.Validation("evidence.report.invalid", error.Message);
            }
        }
        else
        {
            // A failed scanner still leaves a record, so the gate can show exactly which
            // control did not run instead of "evidence missing".
            parsed = new ParsedReport([], command.Metadata.GetValueOrDefault("toolName", command.Kind.ToString()),
                command.Metadata.GetValueOrDefault("toolVersion", "unknown"), null, null, null, null);
            ControlPlaneMetrics.ScannerFailures.Add(1, ControlPlaneMetrics.Tag("kind", command.Kind.ToString()));
        }

        var evidenceId = Guid.CreateVersion7();
        var key = $"{build.Application}/{build.Commit}/{command.Kind}/{command.Digest?.Replace(':', '-') ?? "source"}/{evidenceId:N}";
        var raw = await evidenceStore.StoreAsync(key, command.Report, command.ContentType, cancellationToken);

        var record = EvidenceRecord.Create(build.Id, build.Application, build.Commit, command.Kind, command.Deployable, command.Digest,
            command.Execution, command.ExecutionError,
            new ToolInfo(parsed.ToolName, parsed.ToolVersion, parsed.DatabaseVersion, parsed.DatabaseUpdatedAt),
            raw, caller.Identity, command.PipelineRunId, command.JobName, parsed.GatePassed, parsed.GateDetail, parsed.Findings, clock.GetUtcNow());
        store.Add(record);

        foreach (var artifact in await ArtifactsConcernedAsync(command, cancellationToken))
        {
            artifact.MarkEvidencePending(caller.Identity, clock.GetUtcNow());
        }

        store.Audit(caller.Identity, "evidence.ingested", "evidence", record.Id.ToString(), new
        {
            build = build.Id,
            kind = command.Kind.ToString(),
            commit = build.Commit,
            digest = command.Digest,
            execution = command.Execution.ToString(),
            tool = $"{parsed.ToolName} {parsed.ToolVersion}",
            sha256 = raw.Sha256,
            findings = record.Findings.Count,
            runId = command.PipelineRunId,
        });
        await store.SaveChangesAsync(cancellationToken);

        ControlPlaneMetrics.EvidenceIngested.Add(1, ControlPlaneMetrics.Tag("kind", command.Kind.ToString()), ControlPlaneMetrics.Tag("execution", command.Execution.ToString()));
        foreach (var group in record.Findings.GroupBy(f => f.Severity))
        {
            ControlPlaneMetrics.Findings.Add(group.Count(), ControlPlaneMetrics.Tag("kind", command.Kind.ToString()), ControlPlaneMetrics.Tag("severity", group.Key.ToString()));
        }

        return record;
    }

    private async Task<DomainError?> ValidateAsync(SubmitEvidenceCommand command, Caller caller, CancellationToken cancellationToken)
    {
        if (!ZonePermissions.MaySubmit(caller.Zone, command.Kind))
        {
            return DomainError.Forbidden("evidence.zone.not-allowed", $"The {caller.Zone} zone may not submit {command.Kind} evidence.");
        }

        var build = await store.BuildAsync(command.BuildId, cancellationToken);
        if (build is null)
        {
            return DomainError.NotFound("build.unknown", "Build not found.");
        }

        if (!build.AcceptsRun(command.PipelineRunId))
        {
            return DomainError.Forbidden("build.run.not-dispatched", "Evidence is accepted only from the pipeline run dispatched for this build.");
        }

        if (!string.Equals(command.Commit, build.Commit, StringComparison.Ordinal))
        {
            return DomainError.Validation("evidence.commit.mismatch", $"The build is for commit {build.Commit}, not {command.Commit}.");
        }

        if (EvidenceKinds.SubjectOf(command.Kind) == EvidenceSubject.Artifact)
        {
            var artifacts = await store.ArtifactsForBuildAsync(build.Id, cancellationToken);
            var match = artifacts.FirstOrDefault(a => a.Digest == command.Digest && a.Deployable == command.Deployable);
            if (match is null)
            {
                return DomainError.Validation("evidence.digest.unknown", $"{command.Deployable}@{command.Digest} is not an artifact of this build.");
            }
        }

        return command.Report.Length == 0 && command.Execution == ExecutionStatus.Completed
            ? DomainError.Validation("evidence.report.empty", "A completed scan must include its report.")
            : null;
    }

    private async Task<IReadOnlyList<Domain.Artifacts.Artifact>> ArtifactsConcernedAsync(SubmitEvidenceCommand command, CancellationToken cancellationToken)
    {
        var artifacts = await store.ArtifactsForBuildAsync(command.BuildId, cancellationToken);
        return EvidenceKinds.SubjectOf(command.Kind) == EvidenceSubject.Artifact
            ? artifacts.Where(a => a.Digest == command.Digest).ToList()
            : artifacts;
    }
}
