// Background publisher of one module's outbox.
//
// Only one replica publishes a given outbox at a time: the batch runs inside a
// transaction that holds a PostgreSQL advisory lock for that schema. Messages are signed
// just before sending and marked processed only after RabbitMQ confirms them, so a crash
// in between causes a re-send (which consumers' inboxes absorb), never a lost message.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Commerce.BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Commerce.BuildingBlocks.Messaging;

public sealed partial class OutboxPublisher<TContext>(
    IServiceScopeFactory scopes,
    RabbitMqConnectionProvider connections,
    EnvelopeSigner signer,
    TimeProvider clock,
    ILogger<OutboxPublisher<TContext>> logger) : BackgroundService
    where TContext : ModuleDbContext
{
    private const int BatchSize = 50;
    private static readonly ActivitySource Activities = new("Commerce.Messaging");
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                published = await PublishBatchAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogPublishFailed(logger, typeof(TContext).Name, error);
                published = 0;
                _channel = null;
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }

            if (published < BatchSize)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, stoppingToken);
            }
        }
    }

    private async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Advisory lock keyed by schema name: released automatically at commit/rollback.
        var lockKey = StableHash(context.Schema);
        var locked = await context.Database
            .SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({lockKey}) AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (!locked)
        {
            return 0;
        }

        var batch = await context.OutboxMessages
            .Where(message => message.ProcessedAt == null)
            .OrderBy(message => message.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (batch.Count == 0)
        {
            return 0;
        }

        var channel = await GetChannelAsync(cancellationToken);
        foreach (var message in batch)
        {
            try
            {
                await PublishAsync(channel, message, cancellationToken);
                message.ProcessedAt = clock.GetUtcNow();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = error.Message.Length > 2000 ? error.Message[..2000] : error.Message;
                _channel = null;
                break; // keep ordering: later messages wait for this one
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }

    private async Task PublishAsync(IChannel channel, OutboxMessage message, CancellationToken cancellationToken)
    {
        ActivityContext.TryParse(message.TraceParent, null, out var parent);
        using var activity = Activities.StartActivity($"publish {message.RoutingKey}", ActivityKind.Producer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", message.RoutingKey);
        activity?.SetTag("messaging.message.id", message.Id.ToString());

        var envelope = signer.Sign(message.Id, message.RoutingKey, message.OccurredAt, message.CorrelationId, activity?.Id ?? message.TraceParent, message.Payload);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, MessagingJson.Options));
        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            Type = message.RoutingKey,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            CorrelationId = message.CorrelationId,
        };

        // With publisher confirmation tracking enabled this completes only after the broker
        // has accepted the message, and throws if it was rejected.
        await channel.BasicPublishAsync(MessagingOptions.EventsExchange, message.RoutingKey, mandatory: false, properties, body, cancellationToken);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        var connection = await connections.GetConnectionAsync(cancellationToken);
        _channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await Topology.DeclareExchangeAsync(_channel, cancellationToken);
        return _channel;
    }

    private static long StableHash(string value)
    {
        // Deterministic across processes (string.GetHashCode is randomised per process).
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("outbox:" + value));
        return BitConverter.ToInt64(bytes, 0);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the {Outbox} outbox failed; retrying")]
    private static partial void LogPublishFailed(ILogger logger, string outbox, Exception error);
}
