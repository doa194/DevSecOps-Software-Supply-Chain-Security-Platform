// Resource-based authorization for orders: customers may see only their own orders;
// staff with orders:read-any may see all. A denied request answers 404, not 403, so a
// customer cannot probe which order ids exist, and it is still recorded as a security event.
using Commerce.BuildingBlocks.Security;
using Commerce.Modules.Orders.Domain;

namespace Commerce.Modules.Orders.Features;

public static class OrderAccess
{
    public static bool IsOwner(Order order, ICurrentUser user) =>
        user.Subject is not null && string.Equals(order.OwnerSubject, user.Subject, StringComparison.Ordinal);

    public static bool CanView(Order order, ICurrentUser user) =>
        IsOwner(order, user) || user.HasPermission(Permissions.OrdersReadAny);

    public static void ReportDenied(SecurityEventLog securityEvents, ICurrentUser user, Guid orderId, string requirement) =>
        securityEvents.AuthorizationDenied(user.Subject, $"order/{orderId}", requirement);
}

public sealed record OrderLineDto(string Sku, string Name, int Quantity, decimal UnitPrice);

public sealed record OrderDto(
    Guid Id,
    Guid CustomerId,
    string Status,
    string? StatusReason,
    decimal Total,
    string Currency,
    IReadOnlyList<OrderLineDto> Lines,
    DateTimeOffset PlacedAt,
    DateTimeOffset UpdatedAt)
{
    public static OrderDto From(Order order) => new(
        order.Id, order.CustomerId, order.Status.ToString(), order.StatusReason, order.Total, order.Currency,
        order.Lines.Select(l => new OrderLineDto(l.Sku, l.Name, l.Quantity, l.UnitPrice)).ToList(),
        order.PlacedAt, order.UpdatedAt);
}
