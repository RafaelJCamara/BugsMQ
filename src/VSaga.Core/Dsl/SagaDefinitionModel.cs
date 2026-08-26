using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

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

    public Task RunCompensationAsync(ISagaContext<TState> context, IEnumerable<string> statesMostRecentFirst, CancellationToken cancellationToken) =>
        CompensationRunner.RunAsync(Compensations, context, statesMostRecentFirst, cancellationToken);
}
