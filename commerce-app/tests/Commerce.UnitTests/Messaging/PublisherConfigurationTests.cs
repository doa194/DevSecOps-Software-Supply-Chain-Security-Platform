// Keeps config/publishers.yaml in step with the event contracts. If an event exists in
// code but not in the trust configuration, consumers would reject it at run time; if an
// event were listed for two publishers, either could forge it.
using System.Reflection;
using Commerce.SharedKernel.Messaging;

namespace Commerce.UnitTests.Messaging;

public sealed class PublisherConfigurationTests
{
    [Fact]
    public void Every_contract_event_has_exactly_one_allowed_publisher()
    {
        var contractEvents = ContractAssemblies()
            .SelectMany(a => a.GetTypes())
            .Select(t => t.GetCustomAttribute<IntegrationEventAttribute>()?.RoutingKey)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var configured = ReadPublisherEvents();

        var unlisted = contractEvents.Except(configured.Keys).Order().ToList();
        var duplicated = configured.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToList();
        var stale = configured.Keys.Except(contractEvents).ToList();
        Assert.Empty(unlisted);
        Assert.Empty(duplicated);
        Assert.Empty(stale);
    }

    private static IEnumerable<Assembly> ContractAssemblies() =>
    [
        typeof(Commerce.Modules.Identity.Contracts.UserProvisionedV1).Assembly,
        typeof(Commerce.Modules.Customers.Contracts.CustomerRegisteredV1).Assembly,
        typeof(Commerce.Modules.Catalog.Contracts.ProductCreatedV1).Assembly,
        typeof(Commerce.Modules.Inventory.Contracts.StockReservedV1).Assembly,
        typeof(Commerce.Modules.Orders.Contracts.OrderPlacedV1).Assembly,
        typeof(Commerce.Modules.Payments.Contracts.PaymentCapturedV1).Assembly,
        typeof(Commerce.Modules.Documents.Contracts.DocumentGeneratedV1).Assembly,
        typeof(Commerce.Modules.Administration.Contracts.FeatureFlagChangedV1).Assembly,
        typeof(Commerce.SharedKernel.Auditing.AuditRecordedV1).Assembly,
    ];

    // Minimal reader for the simple list structure of publishers.yaml.
    private static Dictionary<string, int> ReadPublisherEvents()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "publishers.yaml")))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var key = trimmed[2..].Trim();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return counts;
    }
}
