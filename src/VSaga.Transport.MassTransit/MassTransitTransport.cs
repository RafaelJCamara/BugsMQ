using System.Text.Json;
using VSaga.Abstractions.Transport;
using global::MassTransit;
using Microsoft.Extensions.Logging;

namespace VSaga.Transport.MassTransit;

/// <summary>
/// MassTransit-based transport, built entirely on MassTransit's own <see cref="IBus"/>
/// (<see cref="IPublishEndpoint"/>/<see cref="ISendEndpointProvider"/>) for outbound and
/// <see cref="IConsumer{T}"/>/<see cref="ConsumeContext{T}"/> for inbound, over its RabbitMQ transport —
/// never MassTransit's own Courier or saga/state-machine features (see
/// <see cref="IMessageTransport"/>'s own doc comment). Every VSaga message travels as one fixed
/// MassTransit contract, <see cref="VSagaEnvelopeMessage"/> — see its doc comment for why — with the
/// four VSaga envelope headers (and correlation/message id) riding on MassTransit's own
/// <c>SendContext.Headers</c>/<c>ConsumeContext.Headers</c>, exercising MassTransit's real header
/// pipeline rather than being smuggled through as opaque payload data.
/// </summary>
public sealed class MassTransitTransport(
    IBus bus,
    ILogger<MassTransitTransport> logger) : IMessageTransport
{
    public const string CorrelationIdHeader = "x-vsaga-correlation-id";
    public const string MessageIdHeader = "x-vsaga-message-id";

    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return PublishInternalAsync(messageType.Name, body, envelope, cancellationToken);
    }

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return SendInternalAsync(destination, messageType.Name, body, envelope, cancellationToken);
    }

    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishInternalAsync(messageTypeName, body.ToArray(), envelope, cancellationToken);

    private async Task PublishInternalAsync(string messageTypeName, byte[] body, MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var envelopeMessage = new VSagaEnvelopeMessage(messageTypeName, body);

        try
        {
            await bus.Publish(envelopeMessage, ctx =>
            {
                ApplyEnvelope(ctx, envelope);
                ctx.Mandatory = true;
            }, cancellationToken);
        }
        catch (MessageReturnedException ex)
        {
            logger.LogError(ex, "Publish of {MessageType} for correlation id {CorrelationId} was returned as unroutable by the broker",
                messageTypeName, envelope.CorrelationId);
            throw new MessageTransportPublishException(messageTypeName, envelope.CorrelationId, isUnroutable: true, ex);
        }
    }

    private async Task SendInternalAsync(string destination, string messageTypeName, byte[] body, MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var envelopeMessage = new VSagaEnvelopeMessage(messageTypeName, body);
        var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{destination}"));

        try
        {
            await endpoint.Send(envelopeMessage, ctx =>
            {
                ApplyEnvelope(ctx, envelope);
                if (ctx is RabbitMqSendContext rabbitMqContext)
                    rabbitMqContext.Mandatory = true;
            }, cancellationToken);
        }
        catch (MessageReturnedException ex)
        {
            logger.LogError(ex, "Send of {MessageType} for correlation id {CorrelationId} to queue {Destination} was returned as unroutable by the broker",
                messageTypeName, envelope.CorrelationId, destination);
            throw new MessageTransportPublishException(messageTypeName, envelope.CorrelationId, isUnroutable: true, ex);
        }
    }

    private static void ApplyEnvelope(SendContext<VSagaEnvelopeMessage> ctx, MessageEnvelope envelope)
    {
        ctx.CorrelationId = envelope.CorrelationId;
        if (Guid.TryParse(envelope.MessageId, out var messageId))
            ctx.MessageId = messageId;

        // Redundant with the native CorrelationId/MessageId fields above, same defense-in-depth
        // RabbitMqTransport applies — these exact string values, rather than MassTransit's own
        // Guid-typed round-trip, are what SagaOrchestrator's dedup/redelivery logic reads back out.
        ctx.Headers.Set(CorrelationIdHeader, envelope.CorrelationId.ToString());
        ctx.Headers.Set(MessageIdHeader, envelope.MessageId);

        if (envelope.Headers is not null)
        {
            foreach (var (key, value) in envelope.Headers)
                ctx.Headers.Set(key, value);
        }
    }

    public async Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        var handle = bus.ConnectReceiveEndpoint(subscription.QueueNameHint, e =>
        {
            // No MassTransit-level retry: SagaOrchestrator.HandleInfrastructureFailureAsync already owns
            // bounded redelivery entirely at the application level via PublishRawAsync. A rejected
            // message (see MassTransitAckContext) should land straight in "{queue}_error", not be
            // retried again by MassTransit against Core's own wishes.
            e.UseMessageRetry(r => r.None());

            // Explicit topology only: every VSaga message type shares the one MassTransit contract
            // (VSagaEnvelopeMessage), so the default auto-bind-on-consume would deliver every VSaga
            // message ever published to every subscriber regardless of which types it declared.
            e.ConfigureConsumeTopology = false;
            e.Consumer(() => new VSagaEnvelopeConsumer(handler, subscription.ConsumerName, logger));

            if (e is IRabbitMqReceiveEndpointConfigurator rabbitMqEndpoint)
            {
                foreach (var messageType in subscription.MessageTypes)
                {
                    rabbitMqEndpoint.Bind<VSagaEnvelopeMessage>(x =>
                    {
                        x.RoutingKey = messageType.Name;
                        x.ExchangeType = "topic";
                    });
                }
            }
        });

        await handle.Ready.WaitAsync(cancellationToken);
        return new MassTransitSubscription(handle);
    }

    /// <summary>
    /// Bridges MassTransit's throw-to-fault consumer model onto <see cref="IMessageAckContext"/>'s
    /// explicit ack/nack: MassTransit has no mid-flight "ack now" primitive comparable to RabbitMQ.Client's
    /// channel-level BasicAck, so <see cref="AckAsync"/>/<see cref="NackAsync"/> just record the caller's
    /// decision, and <see cref="VSagaEnvelopeConsumer.Consume"/> turns a recorded nack (or no decision at
    /// all) into a thrown exception once the handler completes, which is what actually routes the message
    /// to MassTransit's fault pipeline instead of auto-acking it.
    /// </summary>
    private sealed class MassTransitAckContext : IMessageAckContext
    {
        private readonly TaskCompletionSource<bool> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AckAsync(CancellationToken cancellationToken = default)
        {
            _outcome.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task NackAsync(bool requeue, CancellationToken cancellationToken = default)
        {
            _outcome.TrySetResult(false);
            return Task.CompletedTask;
        }

        /// <summary>True only if <see cref="AckAsync"/> was actually called; false for a recorded nack or no decision at all.</summary>
        public bool WasAcked() => _outcome.Task is { IsCompletedSuccessfully: true, Result: true };
    }

    private sealed class VSagaEnvelopeConsumer(
        Func<ReceivedMessage, CancellationToken, Task> handler,
        string consumerName,
        ILogger logger) : IConsumer<VSagaEnvelopeMessage>
    {
        public async Task Consume(ConsumeContext<VSagaEnvelopeMessage> context)
        {
            var ackContext = new MassTransitAckContext();
            var headers = ToStringHeaders(context.Headers);
            var correlationId = ParseCorrelationId(context.CorrelationId, headers);
            var messageId = headers.GetValueOrDefault(MessageIdHeader) ?? context.MessageId?.ToString() ?? Guid.NewGuid().ToString("N");

            var received = new ReceivedMessage(
                context.Message.MessageTypeName,
                correlationId,
                messageId,
                context.Message.Body,
                headers,
                ackContext);

            try
            {
                await handler(received, context.CancellationToken);
            }
            catch (Exception ex)
            {
                // Mirrors RabbitMqTransport.DispatchReceivedAsync's own catch: the saga orchestrator
                // already catches its own exceptions and acks/nacks itself, so this only fires for a
                // genuinely unexpected failure in dispatch itself. Wrapping (rather than a bare rethrow)
                // routes it to MassTransit's fault pipeline with the message identity attached, exactly
                // like a recorded nack does below.
                logger.LogError(ex, "Unhandled error dispatching message {MessageId} ({MessageType}) to handler for {ConsumerName}",
                    messageId, received.MessageTypeName, consumerName);
                throw new MassTransitDispatchException(received.MessageTypeName, received.CorrelationId, ex);
            }

            if (!ackContext.WasAcked())
                throw new MassTransitNackException(received.MessageTypeName, received.CorrelationId);
        }

        private static Guid ParseCorrelationId(Guid? contextCorrelationId, IReadOnlyDictionary<string, string> headers)
        {
            if (contextCorrelationId is { } guid)
                return guid;

            return headers.TryGetValue(CorrelationIdHeader, out var raw) && Guid.TryParse(raw, out var parsed)
                ? parsed
                : Guid.Empty;
        }

        private static IReadOnlyDictionary<string, string> ToStringHeaders(Headers headers)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in headers.GetAll())
            {
                if (value is not null)
                    result[key] = value as string ?? value.ToString() ?? string.Empty;
            }

            return result;
        }
    }

    /// <summary>Thrown by <see cref="VSagaEnvelopeConsumer.Consume"/> to turn a recorded nack into a MassTransit fault, since MassTransit only settles a delivery as rejected when the consumer throws.</summary>
    public sealed class MassTransitNackException(string messageTypeName, Guid correlationId)
        : Exception($"Message '{messageTypeName}' for correlation id '{correlationId}' was rejected by its handler.");

    /// <summary>Thrown by <see cref="VSagaEnvelopeConsumer.Consume"/> when the handler itself fails unexpectedly, carrying the message identity MassTransit's own fault logging otherwise lacks.</summary>
    public sealed class MassTransitDispatchException(string messageTypeName, Guid correlationId, Exception innerException)
        : Exception($"Unhandled error dispatching message for correlation id '{correlationId}' ({messageTypeName}).", innerException);

    private sealed class MassTransitSubscription(HostReceiveEndpointHandle handle) : IDisposable
    {
        public void Dispose() => handle.StopAsync().GetAwaiter().GetResult();
    }
}
