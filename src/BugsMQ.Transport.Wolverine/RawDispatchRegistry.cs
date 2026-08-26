using System.Collections.Concurrent;
using System.Text.Json;
using BugsMQ.Abstractions.Transport;
using Wolverine;

namespace BugsMQ.Transport.Wolverine;

/// <summary>
/// Bridges Wolverine's single generic <see cref="RawEnvelopeHandler"/> back to whichever
/// <see cref="TransportSubscription"/> is currently registered for the listener a message arrived on.
/// Keyed by the listener's own URI (<c>Envelope.Listener.Address</c>) rather than anything stamped on the
/// message itself, because one topic-exchange publish can legitimately fan out to several different
/// queues/listeners at once — a header stamped by the publisher couldn't know which of those a given
/// delivery is landing through, only the listener the delivery actually arrived on can.
/// </summary>
public sealed class RawDispatchRegistry
{
    private readonly ConcurrentDictionary<Uri, Func<ReceivedMessage, CancellationToken, Task>> _handlers = new();

    public void Register(Uri listenerUri, Func<ReceivedMessage, CancellationToken, Task> handler) =>
        _handlers[listenerUri] = handler;

    public void Unregister(Uri listenerUri) => _handlers.TryRemove(listenerUri, out _);

    public async Task DispatchAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var address = envelope.Listener?.Address
            ?? throw new InvalidOperationException("BugsMQ Wolverine transport received an envelope with no listener address.");

        if (!_handlers.TryGetValue(address, out var handler))
            throw new InvalidOperationException($"No active BugsMQ subscription is registered for listener '{address}'.");

        var wire = JsonSerializer.Deserialize<WireEnvelope>(envelope.Data ?? [])
            ?? throw new InvalidOperationException("BugsMQ Wolverine transport received an envelope with an empty or malformed body.");

        var ack = new WolverineAckContext();
        var received = new ReceivedMessage(wire.MessageTypeName, wire.CorrelationId, wire.MessageId, wire.Body, wire.Headers, ack);

        await handler(received, cancellationToken);

        if (ack.Outcome == AckOutcome.Nacked)
            throw new WolverineRawMessageRejectedException(wire.MessageTypeName, wire.CorrelationId);
    }
}

/// <summary>Thrown to fault Wolverine's own Handle invocation when the downstream BugsMQ handler nacked — see <see cref="WolverineAckContext"/>.</summary>
public sealed class WolverineRawMessageRejectedException(string messageTypeName, Guid correlationId)
    : Exception($"BugsMQ handler rejected '{messageTypeName}' for correlation id '{correlationId}'; routed to Wolverine's own error queue.")
{
    public string MessageTypeName { get; } = messageTypeName;

    public Guid CorrelationId { get; } = correlationId;
}
