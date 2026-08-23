using System.Diagnostics;
using System.Text.Json;
using BugsMQ.Abstractions.Diagnostics;
using BugsMQ.Abstractions.Notifications;
using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Core.Runtime;

/// <summary>
/// Drives one saga type: deserializes inbound messages, loads/creates the snapshot, calls the saga
/// definition, persists the result with optimistic concurrency, appends to the event log, schedules
/// timeouts, and notifies the dashboard. This is where retry/compensation/concurrency semantics
/// described in the architecture actually live.
/// </summary>
public sealed class SagaOrchestrator<TState>(
    ISagaDefinition<TState> definition,
    ISagaSnapshotStore<TState> snapshotStore,
    ISagaEventLogStore eventLog,
    ISagaTimeoutStore timeoutStore,
    IMessageTransport transport,
    ISagaChangeNotifier notifier,
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<SagaOrchestrator<TState>> logger)
    where TState : SagaState, new()
{
    private readonly Dictionary<string, Type> _messageTypesByName =
        definition.MessageTypes.ToDictionary(t => t.Name, t => t);

    public string SagaType => definition.SagaType;

    public async Task HandleAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        try
        {
            await HandleCoreAsync(received, cancellationToken);
            await received.Ack.AckAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing {MessageType} for saga {SagaType}; nacking for redelivery", received.MessageTypeName, SagaType);
            await received.Ack.NackAsync(requeue: true, cancellationToken);
        }
    }

    /// <summary>Manual, dashboard/API-triggered redrive of a Failed saga: replays the exact message that last failed.</summary>
    public async Task RetryAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var existing = await snapshotStore.FindAsync(correlationId, cancellationToken)
                       ?? throw new SagaNotFoundException(correlationId);

        // Note: this in-process path only redrives a recorded technical failure (StepFailed) — unlike
        // BugsMQ.Dashboard.Api's /retry endpoint, it doesn't yet have the reset-and-replay-from-start
        // fallback for sagas that reached Failed via a normal business transition, so it deliberately
        // stays narrower (Failed only, not TimedOut) to match what it can actually redrive.
        if (existing.Status != SagaStatus.Failed)
            throw new SagaRetryNotAllowedException(correlationId, existing.Status.ToString());

        var timeline = await eventLog.GetTimelineAsync(correlationId, cancellationToken);
        var lastFailure = timeline.LastOrDefault(e => e.EntryType == SagaEntryType.StepFailed);

        if (lastFailure is not { MessageType: not null, PayloadJson: not null })
            throw new InvalidOperationException($"Saga '{correlationId}' has no recorded failed step to retry.");

        if (!_messageTypesByName.TryGetValue(lastFailure.MessageType, out var clrType))
            throw new InvalidOperationException($"Unknown message type '{lastFailure.MessageType}' recorded for saga '{correlationId}'.");

        var message = JsonSerializer.Deserialize(lastFailure.PayloadJson, clrType)
                      ?? throw new InvalidOperationException("Failed to deserialize the message being retried.");

        existing.Status = SagaStatus.Running;

        await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.ManualRetryRequested,
            fromState: existing.CurrentState, messageType: lastFailure.MessageType, messageId: lastFailure.MessageId), cancellationToken);

        await RunStepAsync(existing, message, lastFailure.MessageType, lastFailure.MessageId ?? Guid.NewGuid().ToString("N"),
            new Dictionary<string, string>(), isNew: false, cancellationToken);
    }

    /// <summary>Invoked by the timeout dispatcher for a due, previously-scheduled state timeout.</summary>
    public async Task HandleTimeoutAsync(SagaTimeout timeout, CancellationToken cancellationToken)
    {
        var state = await snapshotStore.FindAsync(timeout.CorrelationId, cancellationToken);

        // The saga may have already moved past this state (its pending timeout is cancelled on
        // transition, but a race with an in-flight timer tick is possible) or been deleted — either
        // way there's nothing to do.
        if (state is null || state.CurrentState != timeout.ForState || state.Status != SagaStatus.Running)
            return;

        var expectedVersion = state.Version;
        await LogAsync(SagaLogEntry.Create(timeout.CorrelationId, SagaType, SagaEntryType.TimeoutFired, fromState: timeout.ForState), cancellationToken);

        var visitedStates = await GetVisitedStatesAsync(timeout.CorrelationId, cancellationToken);
        var context = new SagaContext<TState>(state, timeout.CorrelationId, new Dictionary<string, string>(), visitedStates, services, transport, cancellationToken);

        var outcome = await definition.HandleTimeoutAsync(context, timeout.ForState, cancellationToken);
        if (!outcome.WasHandled)
            return;

        await LogAsync(SagaLogEntry.Create(timeout.CorrelationId, SagaType, SagaEntryType.StepSucceeded,
            fromState: outcome.FromState, toState: outcome.ToState), cancellationToken);

        if (outcome.ToState != outcome.FromState && definition.GetTimeout(outcome.ToState) is { } delay)
        {
            var dueAt = timeProvider.GetUtcNow() + delay;
            await timeoutStore.ScheduleAsync(timeout.CorrelationId, SagaType, outcome.ToState, dueAt, cancellationToken);
            await LogAsync(SagaLogEntry.Create(timeout.CorrelationId, SagaType, SagaEntryType.TimeoutScheduled, toState: outcome.ToState), cancellationToken);
        }

        await PersistAsync(state, isNew: false, expectedVersion, cancellationToken);

        if (outcome.FinalStatus == SagaStatus.Failed)
            BugsMqDiagnostics.SagasFailed.Add(1, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));
        else if (outcome.FinalStatus == SagaStatus.Completed)
            BugsMqDiagnostics.SagasCompleted.Add(1, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));

        await notifier.SagaUpdatedAsync(ToSummary(state), cancellationToken);
    }

    private async Task HandleCoreAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        if (!_messageTypesByName.TryGetValue(received.MessageTypeName, out var clrType))
        {
            logger.LogWarning("Ignoring message of unknown type {MessageType} for saga {SagaType}", received.MessageTypeName, SagaType);
            return;
        }

        var message = JsonSerializer.Deserialize(received.Body.Span, clrType)
                      ?? throw new InvalidOperationException($"Failed to deserialize {received.MessageTypeName}.");

        var correlationId = received.CorrelationId;
        var existing = await snapshotStore.FindAsync(correlationId, cancellationToken);
        var isNew = existing is null;

        if (existing is null)
        {
            if (!definition.CanInitiate(clrType))
            {
                await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.UnexpectedEvent,
                    messageType: received.MessageTypeName, messageId: received.MessageId), cancellationToken);
                return;
            }

            var now = timeProvider.GetUtcNow();
            existing = new TState
            {
                CorrelationId = correlationId,
                SagaType = SagaType,
                Kind = definition.Kind,
                CurrentState = definition.InitialStateName,
                Status = SagaStatus.Running,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            // PayloadJson is recorded here (not just on StepFailed) so a saga that later reaches a
            // terminal Failed state through a normal business transition — no exception, so no
            // StepFailed entry to redrive — can still be retried by the dashboard: it replays this
            // exact initiating message against the saga once reset back to its initial state.
            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.SagaStarted,
                toState: existing.CurrentState, messageType: received.MessageTypeName, messageId: received.MessageId,
                payloadJson: System.Text.Encoding.UTF8.GetString(received.Body.Span)), cancellationToken);
        }
        else if (await eventLog.IsDuplicateAsync(correlationId, received.MessageId, cancellationToken))
        {
            logger.LogDebug("Skipping duplicate message {MessageId} for saga {CorrelationId}", received.MessageId, correlationId);
            return;
        }

        await RunStepAsync(existing, message, received.MessageTypeName, received.MessageId, received.Headers, isNew, cancellationToken);
    }

    private async Task RunStepAsync(TState state, object message, string messageTypeName, string messageId,
        IReadOnlyDictionary<string, string> headers, bool isNew, CancellationToken cancellationToken)
    {
        var correlationId = state.CorrelationId;
        var expectedVersion = state.Version;
        var fromState = state.CurrentState;

        // Any message that gets this far (past the initial-vs-existing and duplicate checks) is about
        // to be reprocessed for real — a Failed saga picking one up (via manual RetryAsync's replay or
        // a fresh redelivery of the same message type) is no longer stuck, so optimistically resume
        // Running. If the step fails again, the catch block below sets it back to Failed.
        if (state.Status == SagaStatus.Failed)
            state.Status = SagaStatus.Running;

        await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.MessageReceived,
            messageType: messageTypeName, messageId: messageId), cancellationToken);

        var visitedStates = await GetVisitedStatesAsync(correlationId, cancellationToken);
        var context = new SagaContext<TState>(state, correlationId, headers, visitedStates, services, transport, cancellationToken);

        using var activity = BugsMqDiagnostics.ActivitySource.StartActivity($"saga.step {SagaType}.{fromState}");
        activity?.SetTag(BugsMqDiagnostics.TagSagaType, SagaType);
        activity?.SetTag(BugsMqDiagnostics.TagSagaKind, definition.Kind.ToString());
        activity?.SetTag(BugsMqDiagnostics.TagCorrelationId, correlationId.ToString());
        activity?.SetTag(BugsMqDiagnostics.TagFromState, fromState);

        var stopwatch = Stopwatch.StartNew();
        SagaStepOutcome outcome;

        try
        {
            outcome = await definition.HandleAsync(context, message, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            BugsMqDiagnostics.StepDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));

            state.Status = SagaStatus.Failed;
            var payloadJson = JsonSerializer.Serialize(message, message.GetType());

            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.StepFailed,
                fromState: fromState, messageType: messageTypeName, messageId: messageId,
                payloadJson: payloadJson, errorMessage: ex.Message,
                traceId: activity?.TraceId.ToString(), spanId: activity?.SpanId.ToString()), cancellationToken);

            await PersistAsync(state, isNew, expectedVersion, cancellationToken);
            BugsMqDiagnostics.SagasFailed.Add(1, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));

            await notifier.SagaUpdatedAsync(ToSummary(state), cancellationToken);
            return;
        }

        stopwatch.Stop();
        BugsMqDiagnostics.StepDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));
        activity?.SetTag(BugsMqDiagnostics.TagToState, outcome.ToState);

        if (!outcome.WasHandled)
        {
            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.UnexpectedEvent,
                fromState: fromState, messageType: messageTypeName, messageId: messageId), cancellationToken);
            return;
        }

        await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.StepSucceeded,
            fromState: outcome.FromState, toState: outcome.ToState, messageType: messageTypeName, messageId: messageId,
            traceId: activity?.TraceId.ToString(), spanId: activity?.SpanId.ToString()), cancellationToken);

        if (outcome.ToState != outcome.FromState)
        {
            await timeoutStore.CancelAsync(correlationId, outcome.FromState, cancellationToken);

            if (definition.GetTimeout(outcome.ToState) is { } delay)
            {
                var dueAt = timeProvider.GetUtcNow() + delay;
                await timeoutStore.ScheduleAsync(correlationId, SagaType, outcome.ToState, dueAt, cancellationToken);
                await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.TimeoutScheduled, toState: outcome.ToState), cancellationToken);
            }
        }

        if (outcome.FinalStatus is not null)
            await LogAsync(SagaLogEntry.Create(correlationId, SagaType, SagaEntryType.SagaCompleted, toState: outcome.ToState), cancellationToken);

        await PersistAsync(state, isNew, expectedVersion, cancellationToken);

        if (isNew)
            BugsMqDiagnostics.SagasStarted.Add(1, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));

        switch (outcome.FinalStatus)
        {
            case SagaStatus.Completed:
                BugsMqDiagnostics.SagasCompleted.Add(1, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));
                break;
            case SagaStatus.Failed:
                BugsMqDiagnostics.SagasFailed.Add(1, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, SagaType));
                break;
        }

        await notifier.SagaUpdatedAsync(ToSummary(state), cancellationToken);
    }

    private async Task PersistAsync(TState state, bool isNew, int expectedVersion, CancellationToken cancellationToken)
    {
        state.UpdatedAtUtc = timeProvider.GetUtcNow();

        if (isNew)
            await snapshotStore.InsertAsync(state, cancellationToken);
        else
            await snapshotStore.UpdateAsync(state, expectedVersion, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetVisitedStatesAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var timeline = await eventLog.GetTimelineAsync(correlationId, cancellationToken);

        return timeline
            .Where(e => e.ToState is not null)
            .Select(e => e.ToState!)
            .Distinct()
            .ToList();
    }

    private async Task LogAsync(SagaLogEntry entry, CancellationToken cancellationToken)
    {
        await eventLog.AppendAsync(entry, cancellationToken);
        await notifier.TimelineEntryAddedAsync(entry.CorrelationId, entry, cancellationToken);
    }

    private static SagaSummary ToSummary(TState state) =>
        new(state.CorrelationId, state.SagaType, state.Kind, state.CurrentState, state.Status, state.CreatedAtUtc, state.UpdatedAtUtc, state.Version);
}
