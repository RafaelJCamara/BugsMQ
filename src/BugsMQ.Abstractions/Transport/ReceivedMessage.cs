namespace BugsMQ.Abstractions.Transport;

public interface IMessageAckContext
{
    Task AckAsync(CancellationToken cancellationToken = default);

    Task NackAsync(bool requeue, CancellationToken cancellationToken = default);
}

/// <summary>A raw message as delivered by a transport, before deserialization into a CLR type.</summary>
public sealed record ReceivedMessage(
    string MessageTypeName,
    Guid CorrelationId,
    string MessageId,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> Headers,
    IMessageAckContext Ack);

/// <summary>
/// Declares which message types a consumer wants delivered, and a hint for naming the underlying
/// queue. Not saga-specific — plain participant services (e.g. a sample app's Inventory worker) use
/// this exact same subscription shape to consume commands over <see cref="IMessageTransport"/>.
/// </summary>
public sealed record TransportSubscription(
    string ConsumerName,
    IReadOnlyList<Type> MessageTypes,
    string QueueNameHint);
