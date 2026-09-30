// Time-limited risk acceptance.
//
// A risk exception lets one specific finding (by fingerprint) through the trust gate for
// a limited time, for one application or one of its deployables. It needs a written
// justification, an owner who requests it and a different person who approves it, and it
// always expires. An exception is honoured only while it is approved AND unexpired; the
// clock check means it stops working the moment it expires, even before the background
// job marks it Expired.
using Sscp.ControlPlane.Domain.Common;
using Sscp.ControlPlane.Domain.Evidence;
using Sscp.ControlPlane.Domain.Policy;

namespace Sscp.ControlPlane.Domain.Exceptions;

public enum ExceptionStatus
{
    Requested,
    Approved,
    Rejected,
    Revoked,
    Expired,
}

public sealed record ExceptionStatusChanged(Guid ExceptionId, ExceptionStatus From, ExceptionStatus To, string Actor, string? Note, DateTimeOffset At);

public sealed class RiskException
{
    private readonly List<ExceptionStatusChanged> _changes = [];

    private RiskException() { }

    public Guid Id { get; private set; }
    public string Application { get; private set; } = string.Empty;

    // Null: every deployable of the application.
    public string? Deployable { get; private set; }
    public string FindingFingerprint { get; private set; } = string.Empty;
    public EvidenceKind Kind { get; private set; }
    public string Justification { get; private set; } = string.Empty;
    public string? CompensatingControls { get; private set; }
    public string Owner { get; private set; } = string.Empty;
    public string? Approver { get; private set; }
    public ExceptionStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public string? DecisionNote { get; private set; }
    public int Version { get; private set; }

    public IReadOnlyList<ExceptionStatusChanged> PendingChanges => _changes;

    public static Outcome<RiskException> Request(
        string application, string? deployable, string findingFingerprint, EvidenceKind kind, string justification,
        string? compensatingControls, string owner, DateTimeOffset expiresAt, ExceptionPolicy policy, DateTimeOffset now)
    {
        if (!policy.AllowedKinds.Contains(kind))
        {
            return DomainError.Validation("exception.kind.not-allowed", $"Findings of kind {kind} cannot be accepted as risk (for example leaked secrets must be fixed).");
        }

        if (string.IsNullOrWhiteSpace(justification) || justification.Trim().Length < policy.MinimumJustificationLength)
        {
            return DomainError.Validation("exception.justification.too-short", $"A justification of at least {policy.MinimumJustificationLength} characters is required.");
        }

        if (expiresAt <= now)
        {
            return DomainError.Validation("exception.expiry.past", "An exception must expire in the future.");
        }

        if (expiresAt > now.AddDays(policy.MaxDays))
        {
            return DomainError.Validation("exception.expiry.too-long", $"Exceptions may last at most {policy.MaxDays} days; there are no permanent suppressions.");
        }

        if (string.IsNullOrWhiteSpace(findingFingerprint))
        {
            return DomainError.Validation("exception.finding.missing", "An exception must name exactly one finding.");
        }

        var exception = new RiskException
        {
            Id = Guid.CreateVersion7(),
            Application = application,
            Deployable = deployable,
            FindingFingerprint = findingFingerprint,
            Kind = kind,
            Justification = justification.Trim(),
            CompensatingControls = compensatingControls?.Trim(),
            Owner = owner,
            Status = ExceptionStatus.Requested,
            RequestedAt = now,
            ExpiresAt = expiresAt,
        };
        exception._changes.Add(new ExceptionStatusChanged(exception.Id, ExceptionStatus.Requested, ExceptionStatus.Requested, owner, "requested", now));
        return exception;
    }

    public Outcome Approve(string approver, string? note, DateTimeOffset now)
    {
        if (string.Equals(approver, Owner, StringComparison.Ordinal))
        {
            return DomainError.Forbidden("exception.approval.self", "The owner of an exception cannot approve it.");
        }

        if (now >= ExpiresAt)
        {
            return DomainError.Conflict("exception.approval.expired", "The exception has already expired.");
        }

        return Move(ExceptionStatus.Requested, ExceptionStatus.Approved, approver, note, now);
    }

    public Outcome Reject(string approver, string? note, DateTimeOffset now) =>
        Move(ExceptionStatus.Requested, ExceptionStatus.Rejected, approver, note, now);

    public Outcome Revoke(string actor, string? note, DateTimeOffset now) =>
        Move(ExceptionStatus.Approved, ExceptionStatus.Revoked, actor, note, now);

    public Outcome Expire(DateTimeOffset now) =>
        now >= ExpiresAt && Status is ExceptionStatus.Approved or ExceptionStatus.Requested
            ? Move(Status, ExceptionStatus.Expired, "service:controlplane", "expiry reached", now)
            : DomainError.Conflict("exception.expire.not-due", "The exception is not due to expire.");

    public bool IsValidAt(DateTimeOffset now) => Status == ExceptionStatus.Approved && now < ExpiresAt;

    public bool Covers(string application, string deployable, string fingerprint) =>
        string.Equals(Application, application, StringComparison.Ordinal)
        && (Deployable is null || string.Equals(Deployable, deployable, StringComparison.Ordinal))
        && string.Equals(FindingFingerprint, fingerprint, StringComparison.Ordinal);

    private Outcome Move(ExceptionStatus expected, ExceptionStatus to, string actor, string? note, DateTimeOffset now)
    {
        if (Status != expected)
        {
            return DomainError.Conflict("exception.transition.invalid", $"An exception that is {Status} cannot become {to}.");
        }

        _changes.Add(new ExceptionStatusChanged(Id, Status, to, actor, note, now));
        Status = to;
        DecidedAt = now;
        DecisionNote = note ?? DecisionNote;
        if (to is ExceptionStatus.Approved or ExceptionStatus.Rejected)
        {
            Approver = actor;
        }

        Version++;
        return Outcome.Ok();
    }

    public void ClearPendingChanges() => _changes.Clear();
}
