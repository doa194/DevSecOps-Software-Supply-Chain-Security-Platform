// Local record of a Keycloak user, created on first contact ("just-in-time provisioning").
// Keycloak stays the source of truth for credentials and roles; this record gives other
// parts of the workload a stable account to refer to and a last-seen timestamp.
using Commerce.SharedKernel.Domain;

namespace Commerce.Modules.Identity.Domain;

public sealed record UserProvisioned(string Subject, string Username, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed class UserAccount : AggregateRoot<Guid>
{
    private UserAccount() { }

    public string Subject { get; private set; } = string.Empty;
    public string Username { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public static UserAccount Provision(string subject, string username, DateTimeOffset now)
    {
        var account = new UserAccount
        {
            Id = Guid.CreateVersion7(),
            Subject = subject,
            Username = username,
            CreatedAt = now,
            LastSeenAt = now,
        };
        account.Raise(new UserProvisioned(subject, username, now));
        return account;
    }

    public void Seen(DateTimeOffset now)
    {
        // Only record once per minute to avoid a database write on every request.
        if (now - LastSeenAt > TimeSpan.FromMinutes(1))
        {
            LastSeenAt = now;
            Touch();
        }
    }
}

// Rules for changing a user's roles. Kept in the domain so they are unit tested.
public static class RoleChangePolicy
{
    public sealed record Plan(IReadOnlyList<string> Add, IReadOnlyList<string> Remove);

    public static SharedKernel.Results.Result<Plan> Evaluate(string actorSubject, string targetSubject, IReadOnlySet<string> knownRoles, IEnumerable<string> currentRoles, IEnumerable<string> requestedRoles)
    {
        // Nobody may change their own roles: prevents an administrator from quietly
        // granting themselves finance rights (separation of duties).
        if (string.Equals(actorSubject, targetSubject, StringComparison.Ordinal))
        {
            return SharedKernel.Results.Error.Forbidden("identity.roles.self-change", "You cannot change your own roles.");
        }

        var requested = requestedRoles.Distinct(StringComparer.Ordinal).ToList();
        var unknown = requested.Where(role => !knownRoles.Contains(role)).ToList();
        if (unknown.Count > 0)
        {
            return SharedKernel.Results.Error.Validation("identity.roles.unknown", $"Unknown roles: {string.Join(", ", unknown)}.");
        }

        var current = currentRoles.Where(knownRoles.Contains).ToHashSet(StringComparer.Ordinal);
        return new Plan(
            requested.Where(role => !current.Contains(role)).Order(StringComparer.Ordinal).ToList(),
            current.Where(role => !requested.Contains(role)).Order(StringComparer.Ordinal).ToList());
    }
}
