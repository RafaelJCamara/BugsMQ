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
        PublishInternalAsync(message, destination: null, cancellationToken);

    public Task SendAsync<TMessage>(string destination, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
        PublishInternalAsync(message, destination, cancellationToken);

    Task ISagaContextLogSink.LogAsync(SagaLogEntry entry, CancellationToken cancellationToken) => logAsync(entry, cancellationToken);

    private async Task PublishInternalAsync<TMessage>(TMessage message, string? destination, CancellationToken cancellationToken) where TMessage : notnull
    {
        var effectiveCt = cancellationToken == default ? CancellationToken : cancellationToken;
        var envelope = MessageEnvelope.From(sagaType, CorrelationId, inboundMessageId);

        if (destination is null)
            await transport.PublishAsync(message, envelope, effectiveCt);
        else
            await transport.SendAsync(destination, message, envelope, effectiveCt);

        // Logged only after the transport call succeeds — a publish that throws never happened, so it
        // must not leave a MessagePublished/MessageSent trace behind (the step-level failure path logs
        // its own StepFailed entry for that case).
        var entryType = destination is null ? SagaEntryType.MessagePublished : SagaEntryType.MessageSent;
        await logAsync(SagaLogEntry.Create(CorrelationId, sagaType, entryType,
            messageType: typeof(TMessage).Name, messageId: envelope.MessageId,
            sourceService: sagaType, destinationService: destination, causationId: inboundMessageId), effectiveCt);
    }
}
