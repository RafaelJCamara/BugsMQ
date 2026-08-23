using System.Text.Json;
using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BugsMQ.Transport.RabbitMQ;

/// <summary>
/// RabbitMQ.Client-based transport: one durable topic exchange, one durable queue per consumer with
/// bindings auto-derived from its declared message types, a dead-letter exchange/queue pair per
/// consumer for messages that exhaust RabbitMQ-level redelivery, and correlation/message-id/trace
/// propagation via headers.
/// </summary>
public sealed class RabbitMqTransport(
    RabbitMqConnectionManager connectionManager,
    RabbitMqOptions options,
    IRoutingKeyConvention routingKeyConvention,
    ILogger<RabbitMqTransport> logger) : IMessageTransport
{
    public const string CorrelationIdHeader = "x-bugsmq-correlation-id";
    public const string MessageIdHeader = "x-bugsmq-message-id";
    public const string MessageTypeHeader = "x-bugsmq-message-type";

    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return PublishInternalAsync(messageType.Name, body, envelope, destinationQueue: null, cancellationToken);
    }

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return PublishInternalAsync(messageType.Name, body, envelope, destination, cancellationToken);
    }

    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishInternalAsync(messageTypeName, body, envelope, destinationQueue: null, cancellationToken);

    private async Task PublishInternalAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, string? destinationQueue, CancellationToken cancellationToken)
    {
        var connection = await connectionManager.GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        var props = new BasicProperties
        {
            CorrelationId = envelope.CorrelationId.ToString(),
            MessageId = envelope.MessageId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = BuildHeaders(envelope, messageTypeName),
        };

        if (destinationQueue is not null)
        {
            await channel.BasicPublishAsync(exchange: "", routingKey: destinationQueue, mandatory: false,
                basicProperties: props, body: body, cancellationToken: cancellationToken);
        }
        else
        {
            await EnsureExchangeAsync(channel, cancellationToken);
            var routingKey = routingKeyConvention.GetRoutingKey(messageTypeName);
            await channel.BasicPublishAsync(exchange: options.ExchangeName, routingKey: routingKey, mandatory: false,
                basicProperties: props, body: body, cancellationToken: cancellationToken);
        }
    }

    public async Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        var connection = await connectionManager.GetConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 32, global: false, cancellationToken);
        await EnsureExchangeAsync(channel, cancellationToken);

        var poisonRoutingKey = $"{subscription.ConsumerName}.poison";
        var poisonQueueName = $"{subscription.QueueNameHint}.poison";
        await channel.ExchangeDeclareAsync(options.DeadLetterExchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(poisonQueueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(poisonQueueName, options.DeadLetterExchangeName, poisonRoutingKey, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: subscription.QueueNameHint,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = options.DeadLetterExchangeName,
                ["x-dead-letter-routing-key"] = poisonRoutingKey,
            },
            cancellationToken: cancellationToken);

        foreach (var messageType in subscription.MessageTypes)
        {
            var routingKey = routingKeyConvention.GetRoutingKey(messageType.Name);
            await channel.QueueBindAsync(subscription.QueueNameHint, options.ExchangeName, routingKey, cancellationToken: cancellationToken);
        }

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var headers = ea.BasicProperties.Headers;
            var messageTypeName = GetHeaderString(headers, MessageTypeHeader) ?? ea.RoutingKey;
            var correlationId = ParseCorrelationId(ea.BasicProperties.CorrelationId, headers);
            var messageId = ea.BasicProperties.MessageId ?? GetHeaderString(headers, MessageIdHeader) ?? ea.DeliveryTag.ToString();

            var received = new ReceivedMessage(
                messageTypeName,
                correlationId,
                messageId,
                ea.Body,
                ToStringHeaders(headers),
                new RabbitMqAckContext(channel, ea.DeliveryTag));

            try
            {
                await handler(received, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The saga orchestrator already catches its own exceptions and acks/nacks itself; this
                // only fires for a genuinely unexpected failure in dispatch itself.
                logger.LogError(ex, "Unhandled error dispatching message {MessageId} ({MessageType}) to handler for {ConsumerName}",
                    messageId, messageTypeName, subscription.ConsumerName);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, CancellationToken.None);
            }
        };

        await channel.BasicConsumeAsync(queue: subscription.QueueNameHint, autoAck: false, consumer: consumer, cancellationToken: cancellationToken);

        return new RabbitMqSubscription(channel);
    }

    private Task EnsureExchangeAsync(IChannel channel, CancellationToken cancellationToken) =>
        channel.ExchangeDeclareAsync(options.ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

    private static Dictionary<string, object?> BuildHeaders(MessageEnvelope envelope, string messageTypeName)
    {
        var headers = new Dictionary<string, object?>
        {
            [CorrelationIdHeader] = envelope.CorrelationId.ToString(),
            [MessageIdHeader] = envelope.MessageId,
            [MessageTypeHeader] = messageTypeName,
        };

        if (envelope.Headers is not null)
        {
            foreach (var (key, value) in envelope.Headers)
                headers[key] = value;
        }

        return headers;
    }

    private static Guid ParseCorrelationId(string? correlationIdProperty, IDictionary<string, object?>? headers)
    {
        if (Guid.TryParse(correlationIdProperty, out var fromProperty))
            return fromProperty;

        var fromHeader = GetHeaderString(headers, CorrelationIdHeader);
        return Guid.TryParse(fromHeader, out var parsed) ? parsed : Guid.Empty;
    }

    private static string? GetHeaderString(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            string s => s,
            _ => value.ToString(),
        };
    }

    private static IReadOnlyDictionary<string, string> ToStringHeaders(IDictionary<string, object?>? headers)
    {
        if (headers is null || headers.Count == 0)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(headers.Count);
        foreach (var (key, value) in headers)
        {
            var s = GetHeaderString(headers, key);
            if (s is not null)
                result[key] = s;
        }

        return result;
    }

    private sealed class RabbitMqAckContext(IChannel channel, ulong deliveryTag) : IMessageAckContext
    {
        public Task AckAsync(CancellationToken cancellationToken = default) =>
            channel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken).AsTask();

        public Task NackAsync(bool requeue, CancellationToken cancellationToken = default) =>
            channel.BasicNackAsync(deliveryTag, multiple: false, requeue, cancellationToken).AsTask();
    }

    private sealed class RabbitMqSubscription(IChannel channel) : IDisposable
    {
        public void Dispose() => channel.Dispose();
    }
}
