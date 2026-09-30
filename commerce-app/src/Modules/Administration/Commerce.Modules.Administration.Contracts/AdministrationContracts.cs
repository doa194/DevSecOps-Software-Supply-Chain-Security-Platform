// Public contract of the Administration module.
using Commerce.SharedKernel.Messaging;

namespace Commerce.Modules.Administration.Contracts;

[IntegrationEvent("administration.feature-flag-changed", 1)]
public sealed record FeatureFlagChangedV1(string Name, bool Enabled, string ChangedBy) : IntegrationEvent;

// Feature flags known to the workload. Unknown names cannot be toggled.
public static class FeatureFlags
{
    // Records every price change in a history table.
    public const string CatalogPriceHistory = "Catalog.PriceHistory";

    // Generates an invoice document for every confirmed order.
    public const string DocumentsInvoiceGeneration = "Documents.InvoiceGeneration";

    // Allows finance staff to issue partial refunds (full refunds are always allowed).
    public const string PaymentsPartialRefunds = "Payments.PartialRefunds";

    // Lets customers cancel orders that are already paid (triggers a refund request).
    public const string OrdersCancelAfterPayment = "Orders.CancelAfterPayment";

    public static readonly IReadOnlyList<string> All =
        [CatalogPriceHistory, DocumentsInvoiceGeneration, PaymentsPartialRefunds, OrdersCancelAfterPayment];
}
