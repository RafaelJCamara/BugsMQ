using System.Collections.Concurrent;
using System.Text.Json;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VSaga.Samples.OrderProcessing.Participants;

/// <summary>
/// Base for the sample's downstream services (Inventory/Payment/Shipping). These are plain
/// IMessageTransport consumers, not sagas — they never touch VSaga.Core, which is the point: any
/// service that can publish/subscribe can participate in an orchestrated saga.
/// </summary>
internal abstract class ParticipantService(IMessageTransport transport, string consumerName, ILogger logger) : IHostedService
{
    // Bounds a chaos-duplicated delivery (VSaga.Chaos's Duplicate fault, or a genuine broker at-least-once
    // redelivery) from running a participant's business side effect twice — e.g. reserving inventory or
    // charging a card twice for the one ReserveInventory/ChargePayment command. Process-local and
    // capacity-bounded rather than durable/TTL-based: good enough to absorb the kind of near-immediate
    // redelivery chaos testing injects, not a substitute for a real idempotency store in a production participant.
    private const int MaxTrackedMessageIds = 4096;

    private readonly ConcurrentDictionary<string, byte> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _seenMessageIdOrder = new();

    private IDisposable? _subscription;

    protected abstract string QueueName { get; }

    protected abstract IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var subscription = new TransportSubscription(consumerName, Handlers.Keys.ToList(), QueueName);
        _subscription = await transport.SubscribeAsync(subscription, HandleAsync, cancellationToken);
        logger.LogInformation("{Consumer} listening on {Queue}", consumerName, QueueName);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        var entry = Handlers.FirstOrDefault(h => string.Equals(h.Key.Name, received.MessageTypeName, StringComparison.Ordinal));

        if (entry.Key is null)
        {
            await received.Ack.AckAsync(cancellationToken);
            return;
        }

        if (!TryClaim(received.MessageId))
        {
            logger.LogDebug("{Consumer} skipping duplicate delivery of {MessageId}", consumerName, received.MessageId);
            await received.Ack.AckAsync(cancellationToken);
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize(received.Body.Span, entry.Key)
                          ?? throw new InvalidOperationException($"Failed to deserialize {received.MessageTypeName}.");

            await entry.Value(message, received, cancellationToken);
            await received.Ack.AckAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Consumer} failed handling {MessageType}", consumerName, received.MessageTypeName);
            await received.Ack.NackAsync(requeue: false, cancellationToken);
        }
    }

    /// <summary>Publishes a reply stamped with this participant's identity, causally linked to the inbound message it's responding to — see MessageEnvelope.From.</summary>
    protected Task ReplyAsync<TMessage>(TMessage message, ReceivedMessage received, CancellationToken cancellationToken) where TMessage : notnull =>
        transport.PublishAsync(message, MessageEnvelope.From(consumerName, received.CorrelationId, causationId: received.MessageId), cancellationToken);

    /// <summary>True (and records it) the first time this MessageId is seen; false for a repeat. Evicts the
    /// oldest tracked id once the bound is exceeded, so long-running uptime can't grow this unbounded.</summary>
    private bool TryClaim(string messageId)
    {
        if (!_seenMessageIds.TryAdd(messageId, 0))
            return false;

        _seenMessageIdOrder.Enqueue(messageId);
        while (_seenMessageIdOrder.Count > MaxTrackedMessageIds && _seenMessageIdOrder.TryDequeue(out var oldest))
            _seenMessageIds.TryRemove(oldest, out _);

        return true;
    }
}
