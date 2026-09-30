// Consumes one queue: verifies each message's signature, deserialises only registered
// contract types, processes it once through the inbox, and routes failures to the retry
// or dead-letter queue.
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Commerce.BuildingBlocks.Security;
using Commerce.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Commerce.BuildingBlocks.Messaging;

public sealed class ConsumerDefinition(string queue)
{
    public string Queue { get; } = queue;
    public List<HandlerRegistration> Handlers { get; } = [];

    public ConsumerDefinition Handle<TEvent, THandler, TContext>()
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
        where TContext : Persistence.ModuleDbContext
    {
        Handlers.Add(new HandlerRegistration(IntegrationEventTypes.Describe<TEvent>(), typeof(THandler), typeof(TContext), $"{Queue}/{typeof(THandler).Name}"));
        return this;
    }
}

public sealed partial class IntegrationEventConsumer(
    ConsumerDefinition definition,
    MessagingOptions options,
    RabbitMqConnectionProvider connections,
    EnvelopeVerifier verifier,
    IServiceScopeFactory scopes,
    SecurityEventLog securityEvents,
    ILogger<IntegrationEventConsumer> logger) : BackgroundService
{
    private const string AttemptHeader = "x-sscp-attempt";
    private const string ReasonHeader = "x-sscp-dead-reason";
    private static readonly ActivitySource Activities = new("Commerce.Messaging");
    private static readonly Meter Meter = new("Commerce.Messaging");
    private static readonly Counter<long> Consumed = Meter.CreateCounter<long>("commerce_messages_consumed", description: "Consumed integration messages by queue and outcome");

    private readonly Dictionary<string, HandlerRegistration> _handlers = definition.Handlers.ToDictionary(h => h.Event.RoutingKey, StringComparer.Ordinal);
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await connections.GetConnectionAsync(stoppingToken);
                _channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                    stoppingToken);
                await Topology.DeclareConsumerQueueAsync(_channel, definition.Queue, _handlers.Keys, TimeSpan.FromSeconds(options.RetryDelaySeconds), stoppingToken);
                await _channel.BasicQosAsync(0, options.PrefetchCount, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += (_, delivery) => OnMessageAsync(delivery, stoppingToken);
                await _channel.BasicConsumeAsync(definition.Queue, autoAck: false, consumer, stoppingToken);

                // Stay here until the channel dies (then reconnect) or the host stops.
                while (_channel.IsOpen && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogConsumerFailed(logger, definition.Queue, error);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task OnMessageAsync(BasicDeliverEventArgs delivery, CancellationToken cancellationToken)
    {
        var channel = _channel!;
        MessageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<MessageEnvelope>(delivery.Body.Span, MessagingJson.Options);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null)
        {
            securityEvents.MessageRejected("malformed-envelope", null, null, delivery.BasicProperties.Type, definition.Queue);
            await DeadLetterAsync(channel, delivery, "malformed-envelope", cancellationToken);
            return;
        }

        var failure = verifier.Verify(envelope);
        if (failure != VerificationFailure.None)
        {
            securityEvents.MessageRejected(failure.ToString(), envelope.KeyId, envelope.Source, envelope.Type, definition.Queue);
            await DeadLetterAsync(channel, delivery, $"untrusted:{failure}", cancellationToken);
            return;
        }

        if (!_handlers.TryGetValue(envelope.Type, out var registration))
        {
            await DeadLetterAsync(channel, delivery, "no-handler", cancellationToken);
            return;
        }

        var attempt = ReadAttempt(delivery);
        ActivityContext.TryParse(envelope.TraceParent, null, out var parent);
        using var activity = Activities.StartActivity($"process {envelope.Type}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.message.id", envelope.MessageId.ToString());
        using var correlation = CorrelationContext.Begin(envelope.CorrelationId);

        try
        {
            var integrationEvent = (IntegrationEvent)JsonSerializer.Deserialize(envelope.Payload, registration.Event.ClrType, MessagingJson.Options)!;
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await InboxProcessor.ProcessAsync(
                scope.ServiceProvider, registration, integrationEvent,
                new MessageContext(envelope.MessageId, envelope.Source, envelope.CorrelationId, attempt), cancellationToken);
            Count(outcome == ProcessingOutcome.Duplicate ? "duplicate" : "processed");
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.Message);
            if (attempt < options.MaxDeliveryAttempts)
            {
                LogRetry(logger, envelope.Type, envelope.MessageId, attempt, error);
                await RepublishAsync(channel, delivery, Topology.RetryQueue(definition.Queue), attempt + 1, null, cancellationToken);
                Count("retried");
            }
            else
            {
                LogGaveUp(logger, envelope.Type, envelope.MessageId, attempt, error);
                await DeadLetterAsync(channel, delivery, "max-attempts", cancellationToken);
                return;
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
    }

    private async Task DeadLetterAsync(IChannel channel, BasicDeliverEventArgs delivery, string reason, CancellationToken cancellationToken)
    {
        await RepublishAsync(channel, delivery, Topology.DeadQueue(definition.Queue), ReadAttempt(delivery), reason, cancellationToken);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        Count("dead-lettered");
    }

    private static async Task RepublishAsync(IChannel channel, BasicDeliverEventArgs delivery, string queue, int attempt, string? reason, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
        {
            [AttemptHeader] = attempt,
        };
        if (reason is not null)
        {
            headers[ReasonHeader] = reason;
        }

        var properties = new BasicProperties
        {
            MessageId = delivery.BasicProperties.MessageId,
            Type = delivery.BasicProperties.Type,
            ContentType = delivery.BasicProperties.ContentType,
            CorrelationId = delivery.BasicProperties.CorrelationId,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = headers,
        };
        await channel.BasicPublishAsync(string.Empty, queue, mandatory: false, properties, delivery.Body, cancellationToken);
    }

    private static int ReadAttempt(BasicDeliverEventArgs delivery) =>
        delivery.BasicProperties.Headers?.TryGetValue(AttemptHeader, out var value) == true && value is int attempt ? attempt : 1;

    private void Count(string outcome) =>
        Consumed.Add(1, new KeyValuePair<string, object?>("queue", definition.Queue), new KeyValuePair<string, object?>("outcome", outcome));

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer for {Queue} failed; reconnecting")]
    private static partial void LogConsumerFailed(ILogger logger, string queue, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Processing {MessageType} {MessageId} failed on attempt {Attempt}; scheduling a retry")]
    private static partial void LogRetry(ILogger logger, string messageType, Guid messageId, int attempt, Exception error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing {MessageType} {MessageId} failed {Attempt} times; moved to the dead-letter queue")]
    private static partial void LogGaveUp(ILogger logger, string messageType, Guid messageId, int attempt, Exception error);
}
