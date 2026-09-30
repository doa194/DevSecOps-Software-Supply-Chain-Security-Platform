// RabbitMQ connection and topology.
//
// Topology per consumer queue Q:
//   commerce.events (topic exchange) --routing keys--> Q
//   Q.retry  messages wait here for RetryDelay, then return to Q (dead-letter to Q)
//   Q.dead   parking lot for messages that failed too often or were rejected as untrusted
// Retries are explicit (the consumer republishes with an attempt counter) so the retry
// count is visible and bounded, and poison or forged messages never loop forever.
using RabbitMQ.Client;

namespace Commerce.BuildingBlocks.Messaging;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";
    public const string EventsExchange = "commerce.events";

    // amqp://user:password@host:5672/vhost
    public string ConnectionString { get; set; } = string.Empty;

    // Name of this publisher; must match the Source bound to its key in consumers' trust lists.
    public string Source { get; set; } = string.Empty;

    public SigningOptions Signing { get; set; } = new();
    public Dictionary<string, TrustedPublisher> TrustedPublishers { get; set; } = [];
    public int MaxDeliveryAttempts { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 5;
    public ushort PrefetchCount { get; set; } = 16;
}

public sealed class RabbitMqConnectionProvider(MessagingOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public bool IsConnected => _connection?.IsOpen == true;

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.ConnectionString),
                ClientProvidedName = options.Source,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _lock.Dispose();
    }
}

public static class Topology
{
    public static string RetryQueue(string queue) => $"{queue}.retry";
    public static string DeadQueue(string queue) => $"{queue}.dead";

    public static Task DeclareExchangeAsync(IChannel channel, CancellationToken cancellationToken) =>
        channel.ExchangeDeclareAsync(MessagingOptions.EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

    public static async Task DeclareConsumerQueueAsync(IChannel channel, string queue, IEnumerable<string> routingKeys, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        await DeclareExchangeAsync(channel, cancellationToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(RetryQueue(queue), durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = (int)retryDelay.TotalMilliseconds,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = queue,
            },
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(DeadQueue(queue), durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        foreach (var routingKey in routingKeys)
        {
            await channel.QueueBindAsync(queue, MessagingOptions.EventsExchange, routingKey, cancellationToken: cancellationToken);
        }
    }
}
