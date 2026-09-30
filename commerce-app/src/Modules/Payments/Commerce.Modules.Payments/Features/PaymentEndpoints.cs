// Finance endpoints: view the payment of an order and issue refunds.
// Refunds move money, so they are restricted to the finance role (not even administrators),
// recorded as a privileged operation and written to the security audit trail.
using Commerce.BuildingBlocks.Auditing;
using Commerce.BuildingBlocks.Security;
using Commerce.BuildingBlocks.Web;
using Commerce.Modules.Administration.Contracts;
using Commerce.Modules.Payments.Data;
using Commerce.Modules.Payments.Domain;
using Commerce.SharedKernel.Auditing;
using Commerce.SharedKernel.Classification;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;

namespace Commerce.Modules.Payments.Features;

public static class PaymentEndpoints
{
    public sealed record RefundDto(Guid Id, decimal Amount, [property: FinancialData] string Reason, string RequestedBy, DateTimeOffset RefundedAt);

    public sealed record PaymentDto(
        Guid Id, Guid OrderId, decimal Amount, string Currency, string Method, string Status,
        [property: FinancialData] string TokenFingerprint, string? FailureReason, decimal RefundableAmount, IReadOnlyList<RefundDto> Refunds)
    {
        public static PaymentDto From(Payment p) => new(
            p.Id, p.OrderId, p.Amount, p.Currency, p.Method, p.Status.ToString(), p.TokenFingerprint, p.FailureReason, p.RefundableAmount,
            p.Refunds.Select(r => new RefundDto(r.Id, r.Amount, r.Reason, r.RequestedBy, r.RefundedAt)).ToList());
    }

    public sealed record RefundRequest(decimal Amount, string Reason);

    public sealed class RefundValidator : AbstractValidator<RefundRequest>
    {
        public RefundValidator()
        {
            RuleFor(r => r.Amount).GreaterThan(0);
            RuleFor(r => r.Reason).NotEmpty().MaximumLength(200);
        }
    }

    public static void Map(RouteGroupBuilder payments)
    {
        payments.MapGet("/orders/{orderId:guid}", GetForOrderAsync).RequireAuthorization(Permissions.PaymentsReadAny).WithName("GetPaymentForOrder");
        payments.MapPost("/{paymentId:guid}/refunds", RefundAsync).RequireAuthorization(Permissions.PaymentsRefund).Validate<RefundRequest>().WithName("RefundPayment");
    }

    private static async Task<IResult> GetForOrderAsync(Guid orderId, PaymentsDbContext db, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == orderId, cancellationToken);
        return payment is null ? TypedResults.NotFound() : TypedResults.Ok(PaymentDto.From(payment));
    }

    private static async Task<IResult> RefundAsync(
        Guid paymentId,
        RefundRequest request,
        PaymentsDbContext db,
        AuditTrail<PaymentsDbContext> audit,
        ICurrentUser user,
        IFeatureManager features,
        SecurityEventLog securityEvents,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return TypedResults.NotFound();
        }

        var allowPartial = await features.IsEnabledAsync(FeatureFlags.PaymentsPartialRefunds, cancellationToken);
        var refund = payment.Refund(request.Amount, request.Reason, user.Actor, allowPartial, clock.GetUtcNow());
        if (refund.IsFailure)
        {
            return refund.Error.ToProblem();
        }

        audit.Record("payments.refund", "payment", paymentId.ToString(), AuditCategories.Security, details: new Dictionary<string, string>
        {
            ["orderId"] = payment.OrderId.ToString(),
            ["amount"] = $"{request.Amount} {payment.Currency}",
        });
        await db.SaveChangesAsync(cancellationToken);
        securityEvents.PrivilegedOperation(user.Actor, "payments.refund", $"payment/{paymentId}");
        return TypedResults.Ok(PaymentDto.From(payment));
    }
}
