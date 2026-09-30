// Payments' part of the order flow: capture the amount when an order awaits payment, and
// publish the outcome and refunds.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.Modules.Orders.Contracts;
using Commerce.Modules.Payments.Contracts;
using Commerce.Modules.Payments.Data;
using Commerce.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Modules.Payments.Integration;

internal sealed class CapturePaymentForOrder(PaymentsDbContext db, PaymentProviderResolver providers, TimeProvider clock) : IIntegrationEventHandler<OrderAwaitingPaymentV1>
{
    public async Task HandleAsync(OrderAwaitingPaymentV1 integrationEvent, MessageContext context, CancellationToken cancellationToken)
    {
        if (await db.Payments.AnyAsync(p => p.OrderId == integrationEvent.OrderId, cancellationToken))
        {
            return; // already captured (or failed) for this order
        }

        var provider = providers.Resolve(integrationEvent.PaymentMethodToken);
        var outcome = provider?.Capture(integrationEvent.Amount, integrationEvent.Currency, integrationEvent.PaymentMethodToken)
            ?? ProviderOutcome.Decline("unsupported payment method");
        db.Payments.Add(Payment.Record(
            integrationEvent.OrderId, integrationEvent.CustomerId, integrationEvent.Amount, integrationEvent.Currency,
            provider?.Method ?? "unknown", integrationEvent.PaymentMethodToken, outcome, clock.GetUtcNow()));
    }
}

internal sealed class PaymentEventTranslation(Outbox<PaymentsDbContext> outbox, AuditTrail<PaymentsDbContext> audit) :
    IDomainEventHandler<PaymentCaptured>,
    IDomainEventHandler<PaymentFailed>,
    IDomainEventHandler<PaymentRefunded>
{
    public Task HandleAsync(PaymentCaptured e, CancellationToken cancellationToken)
    {
        outbox.Add(new PaymentCapturedV1(e.Payment.Id, e.Payment.OrderId, e.Payment.Amount, e.Payment.Currency));
        audit.Record("payments.capture", "payment", e.Payment.Id.ToString(), details: new Dictionary<string, string> { ["orderId"] = e.Payment.OrderId.ToString() });
        return Task.CompletedTask;
    }

    public Task HandleAsync(PaymentFailed e, CancellationToken cancellationToken)
    {
        outbox.Add(new PaymentFailedV1(e.Payment.Id, e.Payment.OrderId, e.Payment.FailureReason ?? "declined"));
        audit.Record("payments.capture", "payment", e.Payment.Id.ToString(), outcome: SharedKernel.Auditing.AuditOutcomes.Failed,
            details: new Dictionary<string, string> { ["orderId"] = e.Payment.OrderId.ToString(), ["reason"] = e.Payment.FailureReason ?? "declined" });
        return Task.CompletedTask;
    }

    public Task HandleAsync(PaymentRefunded e, CancellationToken cancellationToken)
    {
        outbox.Add(new PaymentRefundedV1(e.Payment.Id, e.Payment.OrderId, e.Refund.Id, e.Refund.Amount, e.Payment.Currency, e.Payment.Status == PaymentStatus.Refunded));
        return Task.CompletedTask;
    }
}
