// Business invariants of the other modules: stock reservation, refunds, payment provider
// strategies, catalog validation and customer addresses.
using Commerce.Modules.Catalog.Domain;
using Commerce.Modules.Customers.Domain;
using Commerce.Modules.Inventory.Domain;
using Commerce.Modules.Payments.Domain;
using Commerce.SharedKernel.Domain;

namespace Commerce.UnitTests.Domain;

public sealed class InventoryReservationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Dictionary<string, StockItem> Stock(params (string Sku, int OnHand)[] items) =>
        items.ToDictionary(i => i.Sku, i =>
        {
            var item = StockItem.Track(i.Sku);
            item.Receive(i.OnHand, Now);
            return item;
        });

    [Fact]
    public void Reserves_every_line_when_all_are_available()
    {
        var stock = Stock(("KBD-1001", 5), ("MSE-1002", 5));
        var order = Guid.NewGuid();

        var result = ReservationService.Reserve(order, [new("KBD-1001", 2), new("MSE-1002", 5)], stock, Now);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, stock["KBD-1001"].Available);
        Assert.Equal(0, stock["MSE-1002"].Available);
    }

    [Fact]
    public void Reserves_nothing_when_any_line_is_short()
    {
        var stock = Stock(("KBD-1001", 5), ("MSE-1002", 1));

        var result = ReservationService.Reserve(Guid.NewGuid(), [new("KBD-1001", 2), new("MSE-1002", 2)], stock, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("MSE-1002", result.Error.Message);
        Assert.Equal(5, stock["KBD-1001"].Available);
    }

    [Fact]
    public void Unknown_skus_count_as_unavailable()
    {
        var result = ReservationService.Reserve(Guid.NewGuid(), [new("ZZZ-9999", 1)], Stock(), Now);

        Assert.Equal("ZZZ-9999", result.Error.Message);
    }

    [Fact]
    public void Reserving_twice_for_the_same_order_is_idempotent()
    {
        var stock = Stock(("KBD-1001", 3));
        var order = Guid.NewGuid();

        ReservationService.Reserve(order, [new("KBD-1001", 3)], stock, Now);
        var again = ReservationService.Reserve(order, [new("KBD-1001", 3)], stock, Now);

        Assert.True(again.IsSuccess);
        Assert.Equal(3, stock["KBD-1001"].Reserved);
    }

    [Fact]
    public void Commit_removes_stock_and_release_frees_it()
    {
        var stock = Stock(("KBD-1001", 10));
        var committed = Guid.NewGuid();
        var released = Guid.NewGuid();
        ReservationService.Reserve(committed, [new("KBD-1001", 4)], stock, Now);
        ReservationService.Reserve(released, [new("KBD-1001", 3)], stock, Now);

        stock["KBD-1001"].Commit(committed, Now);
        stock["KBD-1001"].Release(released, Now);

        Assert.Equal(6, stock["KBD-1001"].OnHand);
        Assert.Equal(6, stock["KBD-1001"].Available);
    }
}

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Payment Captured(decimal amount = 100m) =>
        Payment.Record(Guid.NewGuid(), Guid.NewGuid(), amount, "EUR", "card", "card_approved_4242", ProviderOutcome.Approve("ref"), Now);

    [Fact]
    public void Full_refund_marks_the_payment_refunded()
    {
        var payment = Captured();

        var refund = payment.Refund(100m, "requested", "fiona", allowPartial: false, Now);

        Assert.True(refund.IsSuccess);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(0m, payment.RefundableAmount);
    }

    [Fact]
    public void Partial_refunds_are_refused_while_the_feature_is_off()
    {
        var result = Captured().Refund(10m, "goodwill", "fiona", allowPartial: false, Now);

        Assert.Equal("payments.refund.partial-disabled", result.Error.Code);
    }

    [Fact]
    public void Refunds_can_never_exceed_the_captured_amount()
    {
        var payment = Captured();
        payment.Refund(60m, "a", "fiona", allowPartial: true, Now);

        var tooMuch = payment.Refund(41m, "b", "fiona", allowPartial: true, Now);

        Assert.Equal("payments.refund.amount", tooMuch.Error.Code);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
    }

    [Fact]
    public void A_failed_payment_cannot_be_refunded()
    {
        var failed = Payment.Record(Guid.NewGuid(), Guid.NewGuid(), 10m, "EUR", "card", "card_declined_1", ProviderOutcome.Decline("declined"), Now);

        Assert.Equal("payments.refund.invalid-state", failed.Refund(10m, "x", "fiona", true, Now).Error.Code);
    }

    [Fact]
    public void The_token_is_stored_only_as_a_short_fingerprint()
    {
        var payment = Captured();

        Assert.Equal(12, payment.TokenFingerprint.Length);
        Assert.DoesNotContain("4242", payment.TokenFingerprint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("card_approved_4242", 100, true)]
    [InlineData("card_declined_0002", 100, false)]
    [InlineData("card_approved_4242", 5000.01, false)]
    [InlineData("wallet_alice", 500, true)]
    [InlineData("wallet_alice", 500.01, false)]
    public void Providers_apply_their_own_rules(string token, decimal amount, bool approved)
    {
        var resolver = new PaymentProviderResolver([new CardProviderSimulator(), new WalletProviderSimulator()]);

        var outcome = resolver.Resolve(token)!.Capture(amount, "EUR", token);

        Assert.Equal(approved, outcome.Approved);
    }

    [Fact]
    public void Unknown_payment_methods_have_no_provider()
    {
        var resolver = new PaymentProviderResolver([new CardProviderSimulator()]);

        Assert.Null(resolver.Resolve("crypto_abc"));
    }
}

public sealed class CatalogTests
{
    [Theory]
    [InlineData("KBD-1001", true)]
    [InlineData("KBD-123456", true)]
    [InlineData("kbd-1001", false)]
    [InlineData("KBD-12", false)]
    [InlineData("KBD-1001; DROP TABLE", false)]
    public void Sku_format_is_enforced(string value, bool valid) =>
        Assert.Equal(valid, Sku.Create(value).IsSuccess);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.001)]
    [InlineData(100000.01)]
    public void Invalid_prices_are_rejected(decimal amount) =>
        Assert.True(Product.Create(Sku.Create("KBD-1001").Value, "K", "", "c", new Money(amount, "EUR"), DateTimeOffset.UnixEpoch).IsFailure);

    [Fact]
    public void A_retired_product_cannot_be_repriced()
    {
        var product = Product.Create(Sku.Create("KBD-1001").Value, "K", "", "c", new Money(10m, "EUR"), DateTimeOffset.UnixEpoch).Value;
        product.Retire(DateTimeOffset.UnixEpoch);

        Assert.Equal("catalog.product.retired", product.ChangePrice(new Money(12m, "EUR"), "cathy", true, DateTimeOffset.UnixEpoch).Error.Code);
    }

    [Fact]
    public void Price_history_is_recorded_only_when_requested()
    {
        var product = Product.Create(Sku.Create("KBD-1001").Value, "K", "", "c", new Money(10m, "EUR"), DateTimeOffset.UnixEpoch).Value;

        product.ChangePrice(new Money(11m, "EUR"), "cathy", recordHistory: false, DateTimeOffset.UnixEpoch);
        product.ChangePrice(new Money(12m, "EUR"), "cathy", recordHistory: true, DateTimeOffset.UnixEpoch);

        Assert.Single(product.PriceHistory);
        Assert.Equal(11m, product.PriceHistory[0].OldPrice);
    }

    [Fact]
    public void Shopper_search_only_matches_active_products_in_range()
    {
        var active = Product.Create(Sku.Create("KBD-1001").Value, "Keyboard", "", "peripherals", new Money(50m, "EUR"), DateTimeOffset.UnixEpoch).Value;
        active.Activate();
        var draft = Product.Create(Sku.Create("KBD-1002").Value, "Keyboard 2", "", "peripherals", new Money(50m, "EUR"), DateTimeOffset.UnixEpoch).Value;
        var search = ProductSearch.VisibleToShoppers().InCategory("peripherals").PricedBetween(10m, 60m).Matching("keyb");

        Assert.True(search.IsSatisfiedBy(active));
        Assert.False(search.IsSatisfiedBy(draft));
    }
}

public sealed class CustomerTests
{
    [Fact]
    public void The_first_address_becomes_the_default_and_removal_keeps_one_default()
    {
        var customer = Customer.Register("sub", "Carol", "Carol@Example.test", null, DateTimeOffset.UnixEpoch).Value;
        var first = customer.AddAddress("home", "1 Main St", "Town", "12345", "de", makeDefault: false).Value;
        customer.AddAddress("work", "2 Side St", "Town", "12345", "de", makeDefault: false);

        customer.RemoveAddress(first.Id);

        Assert.Equal("carol@example.test", customer.Email);
        Assert.Single(customer.Addresses, a => a.IsDefault);
    }

    [Fact]
    public void At_most_five_addresses_can_be_stored()
    {
        var customer = Customer.Register("sub", "Carol", "c@example.test", null, DateTimeOffset.UnixEpoch).Value;
        for (var i = 0; i < Customer.MaxAddresses; i++)
        {
            customer.AddAddress($"a{i}", "l", "c", "p", "DE", false);
        }

        Assert.Equal("customers.addresses.limit", customer.AddAddress("extra", "l", "c", "p", "DE", false).Error.Code);
    }
}
