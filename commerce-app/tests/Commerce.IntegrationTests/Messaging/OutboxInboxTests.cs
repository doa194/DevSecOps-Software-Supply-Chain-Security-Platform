// End-to-end behaviour of the messaging building blocks against real PostgreSQL and
// RabbitMQ, using the reporting worker's projections as the consumer:
//   outbox row -> signed publish -> verified consume -> inbox -> projection
// plus the failure modes that matter: duplicate delivery and tampered messages.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commerce.BuildingBlocks.Messaging;
using Commerce.BuildingBlocks.Persistence;
using Commerce.BuildingBlocks.Security;
using Commerce.IntegrationTests.Infrastructure;
using Commerce.Modules.Orders.Contracts;
using Commerce.Workers.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Commerce.IntegrationTests.Messaging;

// One running host (outbox publisher + consumer) and one database for the whole class.
public sealed class MessagingHostFixture(InfrastructureFixture infrastructure) : IAsyncLifetime
{
    public const string Queue = "it.reporting";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public IHost Host { get; private set; } = null!;
    public InfrastructureFixture Infrastructure => infrastructure;

    public async ValueTask InitializeAsync()
    {
        var database = await infrastructure.CreateDatabaseAsync("outbox_inbox", "commerce_reporting_worker");
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:ConnectionString"] = infrastructure.RabbitMq.GetConnectionString(),
            ["Messaging:Source"] = "commerce-api",
            ["Messaging:RetryDelaySeconds"] = "1",
            ["Messaging:MaxDeliveryAttempts"] = "3",
            ["Messaging:Signing:KeyId"] = "api-key",
            ["Messaging:Signing:PrivateKeyPem"] = _key.ExportPkcs8PrivateKeyPem(),
            ["Messaging:TrustedPublishers:api-key:Source"] = "commerce-api",
            ["Messaging:TrustedPublishers:api-key:PublicKeyPem"] = _key.ExportSubjectPublicKeyInfoPem(),
            ["Messaging:TrustedPublishers:api-key:AllowedTypes:0"] = "orders.order-placed.v1",
            ["Messaging:TrustedPublishers:api-key:AllowedTypes:1"] = "orders.order-confirmed.v1",
            ["ConnectionStrings:reporting"] = database,
            ["ConnectionStrings:migrations"] = database,
        });
        builder.Services.AddSingleton<SecurityEventLog>();
        builder.Services.AddSingleton(new ServiceIdentity("test"));
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddCommerceMessaging(builder.Configuration, typeof(OrderPlacedV1).Assembly);
        builder.Services.AddModuleDbContext<ReportingDbContext>(builder.Configuration, ReportingDbContext.SchemaName);
        builder.Services.AddOutboxPublisher<ReportingDbContext>();
        builder.Services.AddIntegrationEventConsumer(Queue, consumer => consumer
            .Handle<OrderPlacedV1, ProjectOrderPlaced, ReportingDbContext>()
            .Handle<OrderConfirmedV1, ProjectOrderConfirmed, ReportingDbContext>());
        Host = builder.Build();

        await MigrationRunner.MigrateAsync(Host.Services, builder.Configuration,
            [new(typeof(ReportingDbContext), ReportingDbContext.SchemaName, "commerce_reporting_worker")],
            Host.Services.GetRequiredService<ILogger<MessagingHostFixture>>(), CancellationToken.None);

        // Bind the consumer queue before the host (and its outbox publisher) starts. In
        // production the publisher and consumer are separate long-lived services and the
        // queue is durable, so a message is never published before a binding exists. In this
        // single-host test both start together, so without this the first message could be
        // published to the topic exchange before the consumer has bound its queue and would
        // be dropped. Declaring topology is idempotent, so this only removes the race.
        await DeclareConsumerTopologyAsync();
        await Host.StartAsync();
    }

    private async Task DeclareConsumerTopologyAsync()
    {
        var routingKeys = new[] { IntegrationEventTypes.Describe<OrderPlacedV1>().RoutingKey, IntegrationEventTypes.Describe<OrderConfirmedV1>().RoutingKey };
        var factory = new ConnectionFactory { Uri = new Uri(infrastructure.RabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await Topology.DeclareConsumerQueueAsync(channel, Queue, routingKeys, TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    public MessageEnvelope Sign<TEvent>(TEvent integrationEvent) where TEvent : SharedKernel.Messaging.IntegrationEvent
    {
        using var signer = new EnvelopeSigner(new SigningOptions { KeyId = "api-key", PrivateKeyPem = _key.ExportPkcs8PrivateKeyPem() }, "commerce-api");
        return signer.Sign(integrationEvent.EventId, IntegrationEventTypes.Describe<TEvent>().RoutingKey, integrationEvent.OccurredAt, null, null,
            JsonSerializer.Serialize(integrationEvent, MessagingJson.Options));
    }

    public async Task PublishRawAsync(MessageEnvelope envelope)
    {
        var factory = new ConnectionFactory { Uri = new Uri(infrastructure.RabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, MessagingJson.Options));
        await channel.BasicPublishAsync(MessagingOptions.EventsExchange, envelope.Type, mandatory: false, new BasicProperties { Type = envelope.Type }, body);
    }

    public async Task<uint> DeadLetterCountAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(infrastructure.RabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.MessageCountAsync(Topology.DeadQueue(Queue));
    }

    public async Task<T?> EventuallyAsync<T>(Func<ReportingDbContext, Task<T?>> probe)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            var value = await probe(scope.ServiceProvider.GetRequiredService<ReportingDbContext>());
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(250);
        }

        return default;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
        _key.Dispose();
    }
}

public sealed class OutboxInboxTests(MessagingHostFixture messaging) : IClassFixture<MessagingHostFixture>
{
    [Fact]
    public async Task An_event_written_to_the_outbox_reaches_the_consumer_exactly_once()
    {
        var order = new OrderPlacedV1(Guid.NewGuid(), Guid.NewGuid(), [new OrderLineV1("KBD-1001", "K", 1, 10m)], 10m, "EUR");
        await using (var scope = messaging.Host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<Outbox<ReportingDbContext>>().Add(order);
            await scope.ServiceProvider.GetRequiredService<ReportingDbContext>().SaveChangesAsync();
        }

        var summary = await messaging.EventuallyAsync(db => db.OrderSummaries.FirstOrDefaultAsync(s => s.OrderId == order.OrderId));
        var published = await messaging.EventuallyAsync(db => db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == order.EventId && m.ProcessedAt != null));

        Assert.Equal("Placed", summary?.Status);
        Assert.NotNull(published);
    }

    [Fact]
    public async Task A_message_delivered_twice_is_applied_once()
    {
        var orderId = Guid.NewGuid();
        var sku = $"DUP-{Random.Shared.Next(1000, 9999)}";
        await messaging.PublishRawAsync(messaging.Sign(new OrderPlacedV1(orderId, Guid.NewGuid(), [new OrderLineV1(sku, "M", 2, 5m)], 10m, "USD")));
        await messaging.EventuallyAsync(db => db.OrderSummaries.FirstOrDefaultAsync(s => s.OrderId == orderId));
        var confirmed = messaging.Sign(new OrderConfirmedV1(orderId, Guid.NewGuid(), [new OrderLineV1(sku, "M", 2, 5m)], 10m, "USD"));

        await messaging.PublishRawAsync(confirmed);
        await messaging.PublishRawAsync(confirmed);
        await messaging.EventuallyAsync(db => db.ProductSales.FirstOrDefaultAsync(p => p.Sku == sku));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var product = await messaging.EventuallyAsync(db => db.ProductSales.FirstOrDefaultAsync(p => p.Sku == sku));
        Assert.Equal(2, product?.UnitsSold);
    }

    [Fact]
    public async Task A_tampered_message_is_dead_lettered_and_never_applied()
    {
        var orderId = Guid.NewGuid();
        var envelope = messaging.Sign(new OrderPlacedV1(orderId, Guid.NewGuid(), [new OrderLineV1("KBD-1001", "K", 1, 10m)], 10m, "EUR"));
        var before = await messaging.DeadLetterCountAsync();

        await messaging.PublishRawAsync(envelope with { Payload = envelope.Payload.Replace("\"total\":10", "\"total\":1", StringComparison.Ordinal) });

        uint after = 0;
        for (var attempt = 0; attempt < 40 && after <= before; attempt++)
        {
            await Task.Delay(250);
            after = await messaging.DeadLetterCountAsync();
        }

        Assert.True(after > before);
        Assert.Null(await messaging.EventuallyAsync(async db => await db.OrderSummaries.AnyAsync(s => s.OrderId == orderId) ? "applied" : null));
    }
}
