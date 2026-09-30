// Money as a value object: amount and currency always travel together, and arithmetic
// between different currencies is rejected instead of silently producing a wrong total.
namespace Commerce.SharedKernel.Domain;

public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money Add(Money left, Money right) => left + right;
    public static Money Subtract(Money left, Money right) => left - right;

    public Money Multiply(int quantity) => new(Amount * quantity, Currency);

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot combine {left.Currency} with {right.Currency}.");
        }
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";
}
