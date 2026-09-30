// The order state machine decides which events may move an order and who may cancel it.
// Getting it wrong would let a duplicate message un-cancel an order or let a customer
// cancel an order that was already paid.
using Commerce.Modules.Orders.Domain;
using Commerce.SharedKernel.Domain;
using Commerce.SharedKernel.Results;

namespace Commerce.UnitTests.Orders;

public sealed class OrderStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static Order NewOrder(string token = "card_approved_4242") =>
        Order.Place(Guid.NewGuid(), "owner-sub", [new PricedLine("KBD-1001", "Keyboard", 2, new Money(10m, "EUR"))], token, Now).Value;

    [Fact]
    public void Placing_an_order_prices_lines_on_the_server_and_starts_in_placed()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Placed, order.Status);
        Assert.Equal(20m, order.Total);
        Assert.Single(order.DomainEvents.OfType<OrderPlaced>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Quantities_outside_the_allowed_range_are_rejected(int quantity)
    {
        var result = Order.Place(Guid.NewGuid(), "sub", [new PricedLine("KBD-1001", "K", quantity, new Money(1m, "EUR"))], "card_approved_1", Now);

        Assert.True(result.IsFailure);
        Assert.Equal("orders.lines.quantity", result.Error.Code);
    }

    [Fact]
    public void Mixed_currencies_are_rejected()
    {
        var result = Order.Place(Guid.NewGuid(), "sub",
            [new PricedLine("KBD-1001", "K", 1, new Money(1m, "EUR")), new PricedLine("MSE-1002", "M", 1, new Money(1m, "USD"))], "card_approved_1", Now);

        Assert.Equal("orders.currency.mixed", result.Error.Code);
    }

    [Fact]
    public void The_payment_token_is_erased_once_payment_is_requested()
    {
        var order = NewOrder("card_approved_4242");

        order.MarkStockReserved(Now);

        Assert.Null(order.PaymentMethodToken);
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal("card_approved_4242", order.DomainEvents.OfType<OrderAwaitingPayment>().Single().PaymentMethodToken);
    }

    [Fact]
    public void Happy_path_reaches_fulfilled()
    {
        var order = NewOrder();

        Assert.True(order.MarkStockReserved(Now).IsSuccess);
        Assert.True(order.MarkPaid(Now).IsSuccess);
        Assert.True(order.Fulfil("olga", Now).IsSuccess);

        Assert.Equal(OrderStatus.Fulfilled, order.Status);
    }

    [Fact]
    public void A_late_or_duplicate_event_cannot_move_a_final_order()
    {
        var order = NewOrder();
        order.Reject("out-of-stock", Now);

        var duplicate = order.MarkStockReserved(Now);

        Assert.True(duplicate.IsFailure);
        Assert.Equal(ErrorType.Conflict, duplicate.Error.Type);
        Assert.Equal(OrderStatus.Rejected, order.Status);
    }

    [Fact]
    public void A_failed_payment_cancels_the_order_and_releases_reserved_stock()
    {
        var order = NewOrder();
        order.MarkStockReserved(Now);

        order.MarkPaymentFailed("card declined", Now);

        var cancelled = order.DomainEvents.OfType<OrderCancelled>().Single();
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.True(cancelled.StockWasReserved);
    }

    [Fact]
    public void Owner_may_cancel_before_payment()
    {
        var order = NewOrder();

        var result = order.Cancel("owner-sub", isOwner: true, isStaff: false, ownerMayCancelAfterPayment: false, Now);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Owner_may_not_cancel_a_paid_order_unless_the_business_allows_it()
    {
        var order = NewOrder();
        order.MarkStockReserved(Now);
        order.MarkPaid(Now);

        var denied = order.Cancel("owner-sub", isOwner: true, isStaff: false, ownerMayCancelAfterPayment: false, Now);
        var allowed = order.Cancel("owner-sub", isOwner: true, isStaff: false, ownerMayCancelAfterPayment: true, Now);

        Assert.Equal(ErrorType.Forbidden, denied.Error.Type);
        Assert.True(allowed.IsSuccess);
    }

    [Fact]
    public void Staff_may_cancel_a_paid_order_but_nobody_may_cancel_a_fulfilled_one()
    {
        var paid = NewOrder();
        paid.MarkStockReserved(Now);
        paid.MarkPaid(Now);
        var fulfilled = NewOrder();
        fulfilled.MarkStockReserved(Now);
        fulfilled.MarkPaid(Now);
        fulfilled.Fulfil("olga", Now);

        Assert.True(paid.Cancel("olga", isOwner: false, isStaff: true, ownerMayCancelAfterPayment: false, Now).IsSuccess);
        Assert.Equal(ErrorType.Conflict, fulfilled.Cancel("olga", isOwner: false, isStaff: true, ownerMayCancelAfterPayment: true, Now).Error.Type);
    }
}
