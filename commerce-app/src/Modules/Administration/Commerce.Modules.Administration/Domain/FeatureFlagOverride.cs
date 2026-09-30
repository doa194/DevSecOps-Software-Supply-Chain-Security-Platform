// A runtime override of a feature flag's default value, with who changed it and why.
using Commerce.Modules.Administration.Contracts;
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.Modules.Administration.Domain;

public sealed record FeatureFlagChanged(string Name, bool Enabled, string ChangedBy, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed class FeatureFlagOverride : AggregateRoot<string>
{
    private FeatureFlagOverride() { }

    public bool Enabled { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string UpdatedBy { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<FeatureFlagOverride> Create(string name, bool enabled, string reason, string actor, DateTimeOffset now)
    {
        if (!FeatureFlags.All.Contains(name))
        {
            return Error.NotFound("administration.feature.unknown", $"Unknown feature flag '{name}'.");
        }

        var flag = new FeatureFlagOverride { Id = name };
        flag.Set(enabled, reason, actor, now);
        return flag;
    }

    public void Set(bool enabled, string reason, string actor, DateTimeOffset now)
    {
        Enabled = enabled;
        Reason = reason;
        UpdatedBy = actor;
        UpdatedAt = now;
        Touch();
        Raise(new FeatureFlagChanged(Id, enabled, actor, now));
    }
}

public static class FeatureFlagDefaults
{
    // Values used until an administrator overrides them.
    public static readonly IReadOnlyDictionary<string, bool> Values = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        [FeatureFlags.CatalogPriceHistory] = true,
        [FeatureFlags.DocumentsInvoiceGeneration] = true,
        [FeatureFlags.PaymentsPartialRefunds] = false,
        [FeatureFlags.OrdersCancelAfterPayment] = false,
    };
}
