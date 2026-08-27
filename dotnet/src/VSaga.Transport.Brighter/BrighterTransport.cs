using System.Text.Json;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.RMQ.Async;

namespace VSaga.Transport.Brighter;

/// <summary>
/// Paramore.Brighter-based transport: uses Brighter's own transport-level primitives only
/// (<see cref="RmqMessageProducer"/>'s <see cref="IAmAMessageProducerAsync"/> to publish, and
/// <see cref="RmqMessageConsumer"/>'s <see cref="IAmAMessageConsumerAsync"/> to receive/ack/reject) —
/// never Brighter's CommandProcessor dispatch pipeline, its Outbox/Inbox, its request-handler routing,
/// or any workflow/scheduler feature. VSaga.Core's SagaOrchestrator already owns retry, redelivery,
/// compensation, and dedup; this adapter only moves bytes.
///
/// One durable topic exchange (mirroring RabbitMqTransport's shape), one durable queue per consumer
/// bound to a routing key per declared message type PLUS one routing key equal to the queue's own name
/// (so <see cref="SendAsync{TMessage}"/> can address a queue directly without needing a second exchange —
/// see the constructor's remarks for why).
/// </summary>
public sealed class BrighterTransport : IMessageTransport
{
    /// <summary>
    /// Carries the original (PascalCase) CLR message type name in Brighter's <c>MessageHeader.Bag</c>,
    /// since Brighter's own <c>Header.Topic</c> holds the kebab-case routing key, not the type name —
    /// mirrors <c>RabbitMqTransport.MessageTypeHeader</c>'s purpose exactly, just carried via Bag instead
    /// of a raw AMQP header.
    /// </summary>
    public const string MessageTypeHeader = "x-vsaga-message-type";

    private readonly RmqMessagingGatewayConnection _connection;
    private readonly ILogger<BrighterTransport> _logger;

    public BrighterTransport(BrighterOptions options, ILogger<BrighterTransport> logger)
    {
        _logger = logger;

        // A single shared connection descriptor: Brighter's connection pool keys pooled AMQP connections
        // off this (Name + AmpqUri), so every producer/consumer constructed against it reuses the same
        // underlying TCP connection rather than opening a new one each time (confirmed by direct
        // inspection against a live broker).
        //
        // Deviation from RabbitMqTransport: Brighter's producer is bound to exactly one Exchange for its
        // whole lifetime, using the message's Topic as the routing key — there is no per-publish exchange
        // override and no "default/nameless exchange" concept exposed. RabbitMqTransport's direct-send
        // targets the AMQP default exchange directly; that path isn't available here, so direct-to-queue
        // delivery instead relies on SubscribeAsync also binding the queue's own name as an extra routing
        // key on this same topic exchange (see SubscribeAsync below) — functionally equivalent (the
        // message reaches only that one named queue), mechanically different.
        _connection = new RmqMessagingGatewayConnection
        {
            Name = options.ClientProvidedName,
            AmpqUri = new AmqpUriSpecification(new Uri(options.ConnectionString)),
            Exchange = new Exchange(options.ExchangeName, "topic", durable: true),
        };
    }

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

    public Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishInternalAsync(messageTypeName, body, envelope, destinationQueue: destination, cancellationToken);

    private async Task PublishInternalAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, string? destinationQueue, CancellationToken cancellationToken)
    {
        var routingKey = destinationQueue is not null
            ? new RoutingKey(destinationQueue)
            : new RoutingKey(RoutingKeyConvention.GetRoutingKey(messageTypeName));

        var message = BuildOutboundMessage(messageTypeName, body, envelope, routingKey);

        await using var producer = new RmqMessageProducer(_connection);
        await SendWithConfirmationAsync(producer, message, messageTypeName, envelope, routingKey, cancellationToken);
    }

    private static Message BuildOutboundMessage(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, RoutingKey routingKey)
    {
        var header = new MessageHeader(
            new Id(envelope.MessageId),
            routingKey,
            MessageType.MT_EVENT,
            correlationId: new Id(envelope.CorrelationId.ToString()));

        header.Bag[MessageTypeHeader] = messageTypeName;
        if (envelope.Headers is not null)
        {
            foreach (var (key, value) in envelope.Headers)
                header.Bag[key] = value;
        }

        return new Message(header, new MessageBody(body));
    }

    // Brighter's RmqMessageProducer doesn't throw synchronously on an unroutable/nacked publish; instead
    // it awaits the broker's publisher-confirm internally (bounded by
    // RmqPublication.WaitForConfirmsTimeOutInMilliseconds, default 500ms) and raises
    // ISupportPublishConfirmationAsync's confirmation event. That event was confirmed, by direct testing
    // against a live broker, to fire and complete before SendAsync's own awaited Task completes, so no
    // separate timeout or correlation-by-id is needed: each producer instance here is single-use (see the
    // "await using" in the caller), so there is no cross-talk to disambiguate between.
    private async Task SendWithConfirmationAsync(RmqMessageProducer producer, Message message, string messageTypeName, MessageEnvelope envelope, RoutingKey routingKey, CancellationToken cancellationToken)
    {
        PublishConfirmationResult? confirmation = null;
        Task OnConfirmed(PublishConfirmationResult result)
        {
            confirmation = result;
            return Task.CompletedTask;
        }

        ((ISupportPublishConfirmationAsync)producer).OnMessagePublishedAsync += OnConfirmed;
        try
        {
            await producer.SendAsync(message, cancellationToken);
        }
        finally
        {
            ((ISupportPublishConfirmationAsync)producer).OnMessagePublishedAsync -= OnConfirmed;
        }

        // Known gap, see docs/readme-section-brighter.md: Brighter's RmqMessageProducer never sets AMQP's
        // "mandatory" flag, so a message routed to zero bound queues is still confirmed successfully by
        // the broker (Success stays true) — there is, as of Paramore.Brighter.MessagingGateway.RMQ.Async
        // 10.7.0, no equivalent of RabbitMqTransport's mandatory-plus-publisher-confirms unroutable-return
        // detection. The throw below only fires for a genuine broker-side nack (e.g. a queue at its
        // length limit), the one failure mode this package's confirmation event can actually surface.
        if (confirmation is { Success: false })
        {
            var ex = new InvalidOperationException($"Broker did not confirm publish of message id '{envelope.MessageId}' on topic '{routingKey}'.");
            _logger.LogError(ex, "Publish of {MessageType} for correlation id {CorrelationId} was nacked by the broker", messageTypeName, envelope.CorrelationId);
            throw new MessageTransportPublishException(messageTypeName, envelope.CorrelationId, isUnroutable: true, ex);
        }
    }

    public async Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        var channelName = new ChannelName(subscription.QueueNameHint);

        // One routing key per declared message type, plus the queue's own name as an extra routing key —
        // that second one is what lets direct-to-queue sends address this exact queue by name (see the
        // constructor's remarks). The higher-level Subscription config type IAmAChannelFactory consumes
        // only exposes a single RoutingKey, so binding one queue to several routing keys isn't something
        // it can express. RmqMessageConsumer's own constructor can, since it takes a RoutingKeys
        // collection, so this adapter constructs it directly rather than going through
        // IAmAChannelFactory/Subscription, per this track's scope notes allowing the lowest-level
        // primitive the Service Activator itself sits on.
        var routingKeys = subscription.MessageTypes
            .Select(t => new RoutingKey(RoutingKeyConvention.GetRoutingKey(t.Name)))
            .Append(new RoutingKey(subscription.QueueNameHint))
            .Distinct()
            .ToArray();

        var consumer = new RmqMessageConsumer(
            _connection,
            channelName,
            new RoutingKeys(routingKeys),
            isDurable: true,
            makeChannels: OnMissingChannel.Create);

        // RmqMessageConsumer declares its queue/bindings lazily, inside ReceiveAsync's first call
        // (confirmed by direct testing against a live broker: a message published before a consumer's
        // first ReceiveAsync call is silently dropped, because the queue doesn't exist yet at publish
        // time). IMessageTransport.SubscribeAsync's contract requires topology to exist before this
        // method returns to its caller, so force that eagerly with a near-zero-timeout warm-up receive.
        await consumer.ReceiveAsync(TimeSpan.FromMilliseconds(50), cancellationToken);

        // Deliberately not linked to the incoming cancellation token: that token only scopes this setup
        // call, not the subscription's lifetime (mirrors RabbitMqTransport, whose consumer keeps running
        // after SubscribeAsync returns, until the returned IDisposable is disposed).
        var loopCts = new CancellationTokenSource();
        var loopTask = Task.Run(() => ConsumeLoopAsync(consumer, subscription, handler, loopCts.Token), CancellationToken.None);

        return new BrighterSubscription(loopCts, loopTask, consumer);
    }

    // Brighter's IAmAMessageConsumerAsync is pull-based (ReceiveAsync(timeout)), unlike RabbitMQ.Client's
    // push-based consumer that RabbitMqTransport wires up — so this adapter runs its own poll loop,
    // playing the same role Brighter's own Service Activator message pump would (which this track
    // deliberately never brings in, since it belongs to the CommandProcessor/dispatcher stack this
    // adapter must not depend on).
    private async Task ConsumeLoopAsync(RmqMessageConsumer consumer, TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await PollBatchAsync(consumer, subscription, cancellationToken);
            if (batch is null)
                continue;

            foreach (var message in batch)
                await DispatchOneAsync(consumer, subscription, handler, message);
        }
    }

    private async Task<Message[]?> PollBatchAsync(RmqMessageConsumer consumer, TransportSubscription subscription, CancellationToken cancellationToken)
    {
        try
        {
            return await consumer.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling queue {QueueName} for {ConsumerName}; retrying", subscription.QueueNameHint, subscription.ConsumerName);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Falls through to returning null below; the outer loop's own cancellation check ends it.
            }

            return null;
        }
    }

    private async Task DispatchOneAsync(RmqMessageConsumer consumer, TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, Message message)
    {
        // Message.Empty is Brighter's own sentinel for "nothing available this poll" — not a real
        // delivery, so there's nothing here to dispatch or acknowledge.
        if (message.IsEmpty)
            return;

        var received = BuildReceivedMessage(message, consumer);

        try
        {
            await handler(received, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The saga orchestrator already catches its own exceptions and acks/nacks itself; this only
            // fires for a genuinely unexpected failure in dispatch itself, mirroring RabbitMqTransport's
            // own fallback nack in that same situation.
            _logger.LogError(ex, "Unhandled error dispatching message {MessageId} ({MessageType}) to handler for {ConsumerName}",
                received.MessageId, received.MessageTypeName, subscription.ConsumerName);
            await consumer.RejectAsync(message, cancellationToken: CancellationToken.None);
        }
    }

    private static ReceivedMessage BuildReceivedMessage(Message message, RmqMessageConsumer consumer)
    {
        // Brighter's MessageHeader.Bag also carries several of its own CloudEvents-flavored echoes of
        // core header fields on receipt (for example "CorrelationId", "Topic", "cloudEvents_id") that
        // RabbitMqTransport's raw AMQP headers never have. Filtering to the "x-vsaga-" prefix keeps
        // ReceivedMessage.Headers limited to what VSaga itself ever writes (every real VSaga header,
        // including MessageTypeHeader above and Core's own delivery-attempt header, uses this prefix), so
        // redelivery (which round-trips received.Headers back through PublishRawAsync) doesn't also carry
        // that Brighter-internal noise forward as bogus outbound headers.
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in message.Header.Bag)
        {
            if (value is null || !key.StartsWith("x-vsaga-", StringComparison.Ordinal))
                continue;

            headers[key] = value.ToString() ?? string.Empty;
        }

        var messageTypeName = message.Header.Bag.TryGetValue(MessageTypeHeader, out var rawTypeName) && rawTypeName is string typeName
            ? typeName
            : message.Header.Topic.Value;

        var correlationId = Guid.TryParse(message.Header.CorrelationId?.Value, out var parsedCorrelationId) ? parsedCorrelationId : Guid.Empty;

        return new ReceivedMessage(
            messageTypeName,
            correlationId,
            message.Header.MessageId.Value,
            message.Body.Memory,
            headers,
            new BrighterAckContext(consumer, message));
    }

    private sealed class BrighterAckContext(RmqMessageConsumer consumer, Message message) : IMessageAckContext
    {
        public Task AckAsync(CancellationToken cancellationToken = default) =>
            consumer.AcknowledgeAsync(message, cancellationToken);

        // VSaga.Core only ever calls this with requeue:false today (bounded redelivery is entirely
        // application-level, via PublishRawAsync from the saga orchestrator's own infrastructure-failure
        // handling); both branches are implemented for contract-completeness regardless.
        public Task NackAsync(bool requeue, CancellationToken cancellationToken = default) =>
            requeue
                ? consumer.NackAsync(message, cancellationToken)
                : consumer.RejectAsync(message, cancellationToken: cancellationToken);
    }

    private sealed class BrighterSubscription(CancellationTokenSource loopCts, Task loopTask, RmqMessageConsumer consumer) : IDisposable
    {
        public void Dispose()
        {
            loopCts.Cancel();
            try
            {
                loopTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The loop task observed its own cancellation, or a transient poll error mid-shutdown —
                // either way there is nothing left to do here.
            }

            consumer.Dispose();
            loopCts.Dispose();
        }
    }
}
