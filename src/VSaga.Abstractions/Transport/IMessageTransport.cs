namespace VSaga.Abstractions.Transport;

/// <summary>
/// The only thing VSaga.Core depends on to move messages. Concrete adapters (RabbitMQ, in-memory,
/// and later MassTransit/Wolverine) implement this using their own publish/consume primitives —
/// VSaga never uses another bus's native saga/state-machine features, only its transport.
/// </summary>
public interface IMessageTransport
{
    Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull;

    Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull;

    /// <summary>
    /// Publishes a pre-serialized message by type name rather than CLR type — for callers (like the
    /// dashboard's manual-retry endpoint) that know a message's stored JSON and type name but aren't
    /// compiled against the saga assembly that defines it. The receiving saga engine (wherever it
    /// actually runs) picks it up through the exact same subscription/dispatch path as any other
    /// message, matched by type name.
    /// </summary>
    Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a handler for the message types declared in <paramref name="subscription"/>.
    /// Returns a disposable that stops the subscription. Async because real broker adapters (e.g.
    /// RabbitMQ.Client's modern API) need to declare exchanges/queues/bindings and start consuming
    /// before returning.
    /// </summary>
    Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default);
}
