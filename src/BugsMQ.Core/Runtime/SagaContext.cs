using BugsMQ.Abstractions.Sagas;
using BugsMQ.Abstractions.Transport;

namespace BugsMQ.Core.Runtime;

internal sealed class SagaContext<TState>(
    TState saga,
    Guid correlationId,
    IReadOnlyDictionary<string, string> headers,
    IReadOnlyList<string> visitedStates,
    IServiceProvider services,
    IMessageTransport transport,
    CancellationToken cancellationToken) : ISagaContext<TState>
    where TState : SagaState
{
    public TState Saga { get; } = saga;

    public Guid CorrelationId { get; } = correlationId;

    public IReadOnlyList<string> VisitedStates { get; } = visitedStates;

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public IServiceProvider Services { get; } = services;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        transport.PublishAsync(message, MessageEnvelope.New(CorrelationId), cancellationToken == default ? CancellationToken : cancellationToken);

    public Task SendAsync<TMessage>(string destination, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        transport.SendAsync(destination, message, MessageEnvelope.New(CorrelationId), cancellationToken == default ? CancellationToken : cancellationToken);
}
