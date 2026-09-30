// Payment aggregate: one payment per order, with refunds recorded against it.
using System.Security.Cryptography;
using System.Text;
using Commerce.SharedKernel.Classification;
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.Modules.Payments.Domain;

public enum PaymentStatus
{
    Captured,
    Failed,
    PartiallyRefunded,
    Refunded,
}

public sealed record PaymentCaptured(Payment Payment, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record PaymentFailed(Payment Payment, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);
public sealed record PaymentRefunded(Payment Payment, Refund Refund, DateTimeOffset OccurredAt) : DomainEvent(OccurredAt);

public sealed class Refund
{
    public Guid Id { get; init; }
    public decimal Amount { get; init; }
    [FinancialData] public required string Reason { get; init; }
    public required string RequestedBy { get; init; }
    public DateTimeOffset RefundedAt { get; init; }
}

public sealed class Payment : AggregateRoot<Guid>
{
    private readonly List<Refund> _refunds = [];

    private Payment() { }

    public Guid OrderId { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string Method { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; }
    public string? ProviderReference { get; private set; }
    public string? FailureReason { get; private set; }

    // A short hash of the payment token: enough to spot the same card being reused, useless
    // for charging it.
    [FinancialData] public string TokenFingerprint { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<Refund> Refunds => _refunds;
    public decimal RefundedAmount => _refunds.Sum(r => r.Amount);
    public decimal RefundableAmount => Status is PaymentStatus.Captured or PaymentStatus.PartiallyRefunded ? Amount - RefundedAmount : 0m;

    public static Payment Record(Guid orderId, Guid customerId, decimal amount, string currency, string method, string token, ProviderOutcome outcome, DateTimeOffset now)
    {
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = orderId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Method = method,
            Status = outcome.Approved ? PaymentStatus.Captured : PaymentStatus.Failed,
            ProviderReference = outcome.Reference,
            FailureReason = outcome.Approved ? null : outcome.Reason,
            TokenFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12],
            CreatedAt = now,
        };
        payment.Raise(outcome.Approved ? new PaymentCaptured(payment, now) : new PaymentFailed(payment, now));
        return payment;
    }

    public Result<Refund> Refund(decimal amount, string reason, string requestedBy, bool allowPartial, DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Captured or PaymentStatus.PartiallyRefunded))
        {
            return Error.Conflict("payments.refund.invalid-state", $"A payment that is {Status} cannot be refunded.");
        }

        if (amount <= 0 || amount > RefundableAmount)
        {
            return Error.Validation("payments.refund.amount", $"Refund must be between 0.01 and {RefundableAmount}.");
        }

        if (!allowPartial && amount != RefundableAmount)
        {
            return Error.Validation("payments.refund.partial-disabled", "Partial refunds are currently disabled; refund the full remaining amount.");
        }

        var refund = new Refund { Id = Guid.CreateVersion7(), Amount = amount, Reason = reason, RequestedBy = requestedBy, RefundedAt = now };
        _refunds.Add(refund);
        Status = RefundedAmount == Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        Touch();
        Raise(new PaymentRefunded(this, refund, now));
        return refund;
    }
}
