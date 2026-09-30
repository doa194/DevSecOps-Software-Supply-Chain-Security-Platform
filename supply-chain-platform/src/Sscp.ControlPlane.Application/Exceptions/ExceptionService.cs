// Risk-acceptance workflow: request (owner), approve or reject (a different security
// approver), revoke, and automatic expiry. Every transition is audited.
using Sscp.ControlPlane.Application.Metrics;
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Exceptions;

namespace Sscp.ControlPlane.Application.Exceptions;

public sealed record RequestExceptionCommand(
    string Application, string? Deployable, string FindingFingerprint, EvidenceKind Kind, string Justification, string? CompensatingControls, DateTimeOffset ExpiresAt);

public sealed class ExceptionService(IControlPlaneStore store, IApplicationCatalog applications, IPolicyProvider policies, TimeProvider clock)
{
    public async Task<Outcome<RiskException>> RequestAsync(RequestExceptionCommand command, string owner, CancellationToken cancellationToken)
    {
        var application = applications.Find(command.Application);
        if (application is null)
        {
            return DomainError.NotFound("application.unknown", $"Application '{command.Application}' is not registered.");
        }

        if (command.Deployable is not null && !application.Deployables.Contains(command.Deployable))
        {
            return DomainError.Validation("exception.deployable.unknown", $"'{command.Deployable}' is not a deployable of {command.Application}.");
        }

        var requested = RiskException.Request(command.Application, command.Deployable, command.FindingFingerprint, command.Kind, command.Justification,
            command.CompensatingControls, owner, command.ExpiresAt, policies.Current.Exceptions, clock.GetUtcNow());
        if (!requested.Succeeded)
        {
            return requested;
        }

        store.Add(requested.Value);
        await RecordAsync(requested.Value, cancellationToken);
        return requested;
    }

    public Task<Outcome> ApproveAsync(Guid id, string approver, string? note, CancellationToken cancellationToken) =>
        ChangeAsync(id, exception => exception.Approve(approver, note, clock.GetUtcNow()), cancellationToken);

    public Task<Outcome> RejectAsync(Guid id, string approver, string? note, CancellationToken cancellationToken) =>
        ChangeAsync(id, exception => exception.Reject(approver, note, clock.GetUtcNow()), cancellationToken);

    public Task<Outcome> RevokeAsync(Guid id, string actor, string? note, CancellationToken cancellationToken) =>
        ChangeAsync(id, exception => exception.Revoke(actor, note, clock.GetUtcNow()), cancellationToken);

    // Called periodically. Expired exceptions already stop applying the moment they expire
    // (the evaluator checks the clock); this records the fact and makes it visible.
    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken)
    {
        var due = await store.ExceptionsDueAsync(clock.GetUtcNow(), cancellationToken);
        foreach (var exception in due)
        {
            exception.Expire(clock.GetUtcNow());
            await RecordAsync(exception, cancellationToken, save: false);
        }

        if (due.Count > 0)
        {
            await store.SaveChangesAsync(cancellationToken);
        }

        return due.Count;
    }

    private async Task<Outcome> ChangeAsync(Guid id, Func<RiskException, Outcome> change, CancellationToken cancellationToken)
    {
        var exception = await store.ExceptionAsync(id, cancellationToken);
        if (exception is null)
        {
            return DomainError.NotFound("exception.unknown", "Exception not found.");
        }

        var outcome = change(exception);
        if (outcome.Succeeded)
        {
            await RecordAsync(exception, cancellationToken);
        }

        return outcome;
    }

    private async Task RecordAsync(RiskException exception, CancellationToken cancellationToken, bool save = true)
    {
        foreach (var change in exception.PendingChanges)
        {
            store.Audit(change.Actor, $"exception.{change.To.ToString().ToLowerInvariant()}", "exception", exception.Id.ToString(), new
            {
                application = exception.Application,
                deployable = exception.Deployable,
                finding = exception.FindingFingerprint,
                kind = exception.Kind.ToString(),
                owner = exception.Owner,
                expiresAt = exception.ExpiresAt,
                note = change.Note,
            });
            ControlPlaneMetrics.ExceptionEvents.Add(1, ControlPlaneMetrics.Tag("status", change.To.ToString()));
        }

        exception.ClearPendingChanges();
        if (save)
        {
            await store.SaveChangesAsync(cancellationToken);
        }
    }
}
