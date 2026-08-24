using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Core.Runtime;

namespace BugsMQ.Core.Dsl;

/// <summary>Compiled registration table built up by the fluent DSL calls in a saga definition's constructor.</summary>
internal sealed class SagaDefinitionModel<TState> where TState : SagaState, new()
{
    public readonly Dictionary<string, Dictionary<Type, StepDefinition<TState>>> StepsByState = new(StringComparer.Ordinal);

    public readonly Dictionary<string, Func<ISagaContext<TState>, CancellationToken, Task>> Compensations = new(StringComparer.Ordinal);

    public readonly Dictionary<string, (TimeSpan Delay, StepDefinition<TState> Step)> Timeouts = new(StringComparer.Ordinal);

    public string? InitialStateName { get; set; }

    public UnhandledEventPolicy UnhandledEventPolicy { get; set; } = UnhandledEventPolicy.LogAndIgnore;

    public void AddStep(string stateName, StepDefinition<TState> step)
    {
        if (!StepsByState.TryGetValue(stateName, out var byMessageType))
            StepsByState[stateName] = byMessageType = new Dictionary<Type, StepDefinition<TState>>();

        byMessageType[step.MessageType] = step;
    }

    public async Task RunCompensationAsync(ISagaContext<TState> context, IEnumerable<string> statesMostRecentFirst, CancellationToken cancellationToken)
    {
        var statesToCompensate = statesMostRecentFirst.Where(Compensations.ContainsKey).ToList();
        if (statesToCompensate.Count == 0)
            return;

        var log = (ISagaContextLogSink)context;
        var sagaType = context.Saga.SagaType;

        await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.CompensationStarted), cancellationToken);

        foreach (var stateName in statesToCompensate)
            await RunOneCompensationAsync(log, context, sagaType, stateName, cancellationToken);
    }

    /// <summary>
    /// Runs one state's compensation delegate, logging its outcome. Failures are caught (not
    /// propagated) so one failing compensation doesn't abandon the rest — e.g. a failed refund
    /// shouldn't stop the inventory release that follows it — the failure is still fully visible in the
    /// timeline via CompensationStepFailed.
    /// </summary>
    private async Task RunOneCompensationAsync(ISagaContextLogSink log, ISagaContext<TState> context, string sagaType, string stateName, CancellationToken cancellationToken)
    {
        try
        {
            await Compensations[stateName](context, cancellationToken);
            await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.CompensationStepSucceeded, fromState: stateName), cancellationToken);
        }
        catch (Exception ex)
        {
            await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.CompensationStepFailed, fromState: stateName, errorMessage: ex.Message), cancellationToken);
        }
    }
}
