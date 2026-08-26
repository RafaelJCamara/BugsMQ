using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;

namespace VSaga.Core.Runtime;

/// <summary>
/// Lets VSaga.Core.Dsl.SagaDefinitionModel append CompensationStarted/StepSucceeded/StepFailed entries
/// through the same log funnel a SagaContext already uses for outbound-message logging, without
/// widening the public ISagaContext&lt;TState&gt; DSL surface with an engine-internal concern.
/// </summary>
internal interface ISagaContextLogSink
{
    Task LogAsync(SagaLogEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Lets SagaOrchestrator.HandleStepSuccessAsync drain a step's ctx.PublishAfterCommitAsync queue once
/// its own PersistAsync has committed, through the same kind of internal cast ISagaContextLogSink already
/// uses, without widening the public ISagaContext&lt;TState&gt; DSL surface with an engine-internal concern.
/// </summary>
internal interface ISagaContextDeferredPublisher
{
    IReadOnlyList<Func<Task>> DeferredPublishes { get; }
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
    CancellationToken cancellationToken) : ISagaContext<TState>, ISagaContextLogSink, ISagaContextDeferredPublisher
    where TState : SagaState
{
    private readonly List<Func<Task>> _deferredPublishes = [];

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

        return PublishInternalAsync(message, destination: null, envelope, cancellationToken, SagaEntryType.ChildSagaStarted);
    }

    public Task NotifyParentAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull
    {
        // Checked before any I/O: a root saga has no parent to publish under, and there is no sensible
        // id to fall back to — this must fail loudly rather than silently drop the notification or,
        // worse, publish under this saga's own id where nothing is waiting for it.
        if (Saga.ParentCorrelationId is not { } parentCorrelationId)
        {
            throw new InvalidOperationException(
                $"Saga '{sagaType}' correlation '{CorrelationId}' has no parent to notify. " +
                $"NotifyParentAsync can only be called from a saga started via StartChildAsync.");
        }

        // The only correlation id this can publish under is the one the engine already stamped onto
        // this instance — see the "not a general publish-under-any-id overload" note on the interface.
        var envelope = MessageEnvelope.From(sagaType, parentCorrelationId, inboundMessageId);

        return PublishInternalAsync(message, destination: null, envelope, cancellationToken);
    }

    /// <summary>
    /// Queues the publish rather than sending it now — see the interface doc for why. Built eagerly at
    /// call time (correlation id, source service, causation id all come from this instance, exactly like
    /// PublishAsync's own envelope), so only the actual transport call and its timeline entry are
    /// deferred to the drain.
    /// </summary>
    public Task PublishAfterCommitAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull
    {
        var envelope = MessageEnvelope.From(sagaType, CorrelationId, inboundMessageId);
        _deferredPublishes.Add(() => PublishInternalAsync(message, destination: null, envelope, cancellationToken));
        return Task.CompletedTask;
    }

    IReadOnlyList<Func<Task>> ISagaContextDeferredPublisher.DeferredPublishes => _deferredPublishes;

    Task ISagaContextLogSink.LogAsync(SagaLogEntry entry, CancellationToken cancellationToken) => logAsync(entry, cancellationToken);

    private async Task PublishInternalAsync<TMessage>(TMessage message, string? destination, MessageEnvelope envelope, CancellationToken cancellationToken, SagaEntryType? entryTypeOverride = null) where TMessage : notnull
    {
        var effectiveCt = cancellationToken == default ? CancellationToken : cancellationToken;

        if (destination is null)
            await transport.PublishAsync(message, envelope, effectiveCt);
        else
            await transport.SendAsync(destination, message, envelope, effectiveCt);

        // Logged only after the transport call succeeds — a publish that throws never happened, so it
        // must not leave a trace behind (the step-level failure path logs its own StepFailed entry for
        // that case).
        // CorrelationId, not envelope.CorrelationId: the entry belongs on the timeline of the saga that
        // published — for StartChildAsync that's the parent's (the child's own timeline opens with its
        // SagaStarted entry under its own id), and for NotifyParentAsync it's symmetrically the child's
        // own (the parent sees it arrive as an ordinary MessageReceived on its own timeline instead).
        // entryTypeOverride lets StartChildAsync tag its hop ChildSagaStarted instead of the ordinary
        // MessagePublished/MessageSent every other publish gets — see SagaEntryType's Slice 2b note.
        var entryType = entryTypeOverride ?? (destination is null ? SagaEntryType.MessagePublished : SagaEntryType.MessageSent);
        await logAsync(SagaLogEntry.Create(CorrelationId, sagaType, entryType,
            messageType: typeof(TMessage).Name, messageId: envelope.MessageId,
            sourceService: sagaType, destinationService: destination, causationId: inboundMessageId), effectiveCt);
    }
}
