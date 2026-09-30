// Business metrics of the order flow, exported through OpenTelemetry.
using System.Diagnostics.Metrics;

namespace Commerce.Modules.Orders;

internal static class OrderMetrics
{
    private static readonly Meter Meter = new("Commerce.Business");
    public static readonly Counter<long> Placed = Meter.CreateCounter<long>("commerce_orders_placed", description: "Orders placed");
    public static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("commerce_order_outcomes", description: "Orders by outcome (confirmed, rejected, cancelled)");
}
