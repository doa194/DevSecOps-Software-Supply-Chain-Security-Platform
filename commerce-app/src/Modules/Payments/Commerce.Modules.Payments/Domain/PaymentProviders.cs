// Payment providers as strategies.
//
// Justification for the pattern: card and wallet payments follow different rules (a card
// is declined above a manual-review threshold, a wallet has a balance limit) and the
// provider is chosen per payment from the token. Each rule set lives in its own class
// behind one interface, so adding a provider never touches the capture logic.
//
// Both providers are deterministic simulators: the platform is fully local and never
// talks to a real payment network.
namespace Commerce.Modules.Payments.Domain;

public sealed record ProviderOutcome(bool Approved, string? Reference, string? Reason)
{
    public static ProviderOutcome Approve(string reference) => new(true, reference, null);
    public static ProviderOutcome Decline(string reason) => new(false, null, reason);
}

public interface IPaymentProvider
{
    string Method { get; }
    ProviderOutcome Capture(decimal amount, string currency, string token);
}

public sealed class CardProviderSimulator : IPaymentProvider
{
    public const decimal ManualReviewThreshold = 5_000m;

    public string Method => "card";

    public ProviderOutcome Capture(decimal amount, string currency, string token)
    {
        if (amount > ManualReviewThreshold)
        {
            return ProviderOutcome.Decline("amount requires manual review");
        }

        return token.StartsWith("card_approved_", StringComparison.Ordinal)
            ? ProviderOutcome.Approve($"card-{Guid.CreateVersion7():N}")
            : ProviderOutcome.Decline("card declined");
    }
}

public sealed class WalletProviderSimulator : IPaymentProvider
{
    public const decimal WalletLimit = 500m;

    public string Method => "wallet";

    public ProviderOutcome Capture(decimal amount, string currency, string token) =>
        amount <= WalletLimit
            ? ProviderOutcome.Approve($"wallet-{Guid.CreateVersion7():N}")
            : ProviderOutcome.Decline("wallet limit exceeded");
}

public sealed class PaymentProviderResolver(IEnumerable<IPaymentProvider> providers)
{
    private readonly Dictionary<string, IPaymentProvider> _byMethod = providers.ToDictionary(p => p.Method, StringComparer.Ordinal);

    // The token prefix names the method, e.g. "card_approved_4242" -> card.
    public IPaymentProvider? Resolve(string token)
    {
        var method = token.Split('_', 2)[0];
        return _byMethod.GetValueOrDefault(method);
    }
}
