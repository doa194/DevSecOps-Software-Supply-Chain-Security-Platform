// Public contract of the Payments module.
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Payments.Contracts;

[IntegrationEvent("payments.payment-captured", 1)]
public sealed record PaymentCapturedV1(Guid PaymentId, Guid OrderId, decimal Amount, string Currency) : IntegrationEvent;

[IntegrationEvent("payments.payment-failed", 1)]
public sealed record PaymentFailedV1(Guid PaymentId, Guid OrderId, string Reason) : IntegrationEvent;

[IntegrationEvent("payments.payment-refunded", 1)]
public sealed record PaymentRefundedV1(Guid PaymentId, Guid OrderId, Guid RefundId, decimal Amount, string Currency, bool FullyRefunded) : IntegrationEvent;
