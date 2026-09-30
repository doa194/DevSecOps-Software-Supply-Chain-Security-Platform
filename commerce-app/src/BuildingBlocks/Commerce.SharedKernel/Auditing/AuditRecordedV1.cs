// Contract of the dedicated audit trail. Every module publishes audit records through
// its transactional outbox, so an audit record exists if and only if the audited change
// was committed. The audit worker is the only consumer and stores them append-only.
using Commerce.SharedKernel.Messaging;

namespace Commerce.SharedKernel.Auditing;

public static class AuditCategories
{
    // Business changes such as an order being cancelled or a price being changed.
    public const string Business = "business";

    // Security-relevant actions such as role changes, refunds or reading another user's data.
    public const string Security = "security";
}

public static class AuditOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Denied = "denied";
    public const string Failed = "failed";
}

[IntegrationEvent("audit.recorded", 1)]
public sealed record AuditRecordedV1 : IntegrationEvent
{
    // Keycloak subject of the user, or "service:<name>" for automated actions.
    public required string Actor { get; init; }

    // Dotted action name, for example "orders.cancel" or "identity.roles.change".
    public required string Action { get; init; }

    public required string Category { get; init; }
    public required string ResourceType { get; init; }
    public required string ResourceId { get; init; }
    public required string Outcome { get; init; }

    // Identifiers and non-sensitive facts only; personal data never goes into the audit trail.
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();

    public string? CorrelationId { get; init; }
}
