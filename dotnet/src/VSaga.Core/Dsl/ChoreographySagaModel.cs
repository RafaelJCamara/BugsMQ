using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

/// <summary>
/// Compiled registration table built up by a <see cref="ChoreographedSagaDefinition{TState}"/>'s
/// fluent DSL calls. Unlike <see cref="SagaDefinitionModel{TState}"/>, steps are keyed by message type
/// alone, not by (state, message type): a choreography has no central conductor gating which event is
/// "expected" next, so any registered event handler applies regardless of the instance's current
/// recorded state.
/// </summary>
internal sealed class ChoreographySagaModel<TState> where TState : SagaState, new()
{
    public readonly Dictionary<Type, StepDefinition<TState>> StepsByMessageType = new();

    public readonly HashSet<Type> InitiatingMessageTypes = [];

    public readonly Dictionary<string, Func<ISagaContext<TState>, CancellationToken, Task>> Compensations = new(StringComparer.Ordinal);

    public readonly Dictionary<string, (TimeSpan Delay, StepDefinition<TState> Step)> Timeouts = new(StringComparer.Ordinal);

    public readonly SagaCorrelationModel<TState> Correlation = new();

    public string? InitialStateName { get; set; }

    public UnhandledEventPolicy UnhandledEventPolicy { get; set; } = UnhandledEventPolicy.LogAndIgnore;

    public void AddStep(StepDefinition<TState> step) => StepsByMessageType[step.MessageType] = step;

    public Task RunCompensationAsync(ISagaContext<TState> context, IEnumerable<string> statesMostRecentFirst, CancellationToken cancellationToken) =>
        CompensationRunner.RunAsync(Compensations, context, statesMostRecentFirst, cancellationToken);
}
