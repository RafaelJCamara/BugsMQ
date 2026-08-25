using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Abstractions.Transport;

namespace BugsMQ.Core.Runtime;

/// <summary>
/// Lets BugsMQ.Core.Dsl.SagaDefinitionModel append CompensationStarted/StepSucceeded/StepFailed entries
/// through the same log funnel a SagaContext already uses for outbound-message logging, without
/// widening the public ISagaContext&lt;TState&gt; DSL surface with an engine-internal concern.
/// </summary>
internal interface ISagaContextLogSink
{
    Task LogAsync(SagaLogEntry entry, CancellationToken cancellationToken);
}

internal sealed class SagaContext<TState>(
    TState saga,
    Guid correlationId,
    IReadOnlyDictionary<string, string> headers,
    IReadOnlyList<string> visitedStates,
    IServiceProvider services,
    IMessageTransport transport,
    string sagaType,
    string? inboundMessageId,
    Func<SagaLogEntry, CancellationToken, Task> logAsync,
    CancellationToken cancellationToken) : ISagaContext<TState>, ISagaContextLogSink
    where TState : SagaState
{
    public TState Saga { get; } = saga;

    public Guid CorrelationId { get; } = correlationId;

    public IReadOnlyList<string> VisitedStates { get; } = visitedStates;

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public IServiceProvider Services { get; } = services;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        PublishInternalAsync(message, destination: null, MessageEnvelope.From(sagaType, CorrelationId, inboundMessageId), cancellationToken);

    public Task SendAsync<TMessage>(string destination, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        PublishInternalAsync(message, destination, MessageEnvelope.From(sagaType, CorrelationId, inboundMessageId), cancellationToken);

    public Task StartChildAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull
    {
        // A fresh correlation id rather than this saga's. The snapshot primary key is
        // (SagaType, CorrelationId), so sharing one would cap a parent at a single child per saga type
        // and make a self-recursive saga (a RefundSaga starting a RefundSaga) collide with itself.
        // The link back is carried in headers instead, which SagaOrchestrator reads exactly once, when
        // it creates the child instance.
        var linkage = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MessageEnvelope.ParentSagaTypeHeader] = sagaType,
            [MessageEnvelope.ParentCorrelationIdHeader] = CorrelationId.ToString(),
        };

        var envelope = MessageEnvelope.From(sagaType, Guid.NewGuid(), inboundMessageId, linkage);

        return PublishInternalAsync(message, destination: null, envelope, cancellationToken);
    }

    Task ISagaContextLogSink.LogAsync(SagaLogEntry entry, CancellationToken cancellationToken) => logAsync(entry, cancellationToken);

    private async Task PublishInternalAsync<TMessage>(TMessage message, string? destination, MessageEnvelope envelope, CancellationToken cancellationToken) where TMessage : notnull
    {
        var effectiveCt = cancellationToken == default ? CancellationToken : cancellationToken;

        if (destination is null)
            await transport.PublishAsync(message, envelope, effectiveCt);
        else
            await transport.SendAsync(destination, message, envelope, effectiveCt);

        // Logged only after the transport call succeeds — a publish that throws never happened, so it
        // must not leave a MessagePublished/MessageSent trace behind (the step-level failure path logs
        // its own StepFailed entry for that case).
        // CorrelationId, not envelope.CorrelationId: the entry belongs on the timeline of the saga that
        // published, which for StartChildAsync means the parent's — the child's own timeline opens with
        // its SagaStarted entry under its own id. A started child is therefore recorded here as an
        // ordinary MessagePublished, indistinguishable from any other publish; a dedicated
        // ChildSagaStarted entry type is deliberately left to the completion-notification slice, since
        // SagaEntryType persists as plain integers and is append-only.
        var entryType = destination is null ? SagaEntryType.MessagePublished : SagaEntryType.MessageSent;
        await logAsync(SagaLogEntry.Create(CorrelationId, sagaType, entryType,
            messageType: typeof(TMessage).Name, messageId: envelope.MessageId,
            sourceService: sagaType, destinationService: destination, causationId: inboundMessageId), effectiveCt);
    }
}
