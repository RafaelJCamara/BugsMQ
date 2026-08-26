namespace VSaga.Abstractions.Transport;

/// <summary>
/// Pass-through IMessageTransport decorator that observes each SubscribeAsync call to learn which
/// service consumes which message type — every subscriber (orchestrator and participant alike)
/// already declares exactly that in its TransportSubscription, so no subscriber writes a line of code.
/// Takes a plain callback rather than a persistence store directly, so this stays free of any
/// DI/scoping concerns; VSaga.Core's AddVSagaTopologyRecording() supplies a callback that resolves
/// the real store per call.
/// </summary>
public sealed class TopologyRecordingTransport(IMessageTransport inner, Func<TransportSubscription, CancellationToken, Task> onSubscribed) : IMessageTransport
{
    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default) where TMessage : notnull =>
        inner.PublishAsync(message, envelope, cancellationToken);

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default) where TMessage : notnull =>
        inner.SendAsync(destination, message, envelope, cancellationToken);

    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        inner.PublishRawAsync(messageTypeName, body, envelope, cancellationToken);

    public async Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        await onSubscribed(subscription, cancellationToken);
        return await inner.SubscribeAsync(subscription, handler, cancellationToken);
    }
}
