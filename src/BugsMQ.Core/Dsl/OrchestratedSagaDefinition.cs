using BugsMQ.Abstractions.Diagnostics;
using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Core.Dsl;

/// <summary>
/// Base class for a fluently-defined, orchestrated saga. Derive from this, declare states and
/// transitions in the constructor, and register the derived type via
/// <c>services.AddBugsMqEngine(o => o.AddSaga&lt;TDefinition, TState&gt;())</c>.
/// </summary>
public abstract class OrchestratedSagaDefinition<TState> : ISagaDefinition<TState>
    where TState : SagaState, new()
{
    private readonly SagaDefinitionModel<TState> _model = new();

    public string SagaType { get; }

    public SagaKind Kind => SagaKind.Orchestrated;

    protected OrchestratedSagaDefinition(string? sagaType = null)
    {
        SagaType = sagaType ?? GetType().Name;
    }

    public string InitialStateName =>
        _model.InitialStateName ?? throw new SagaDefinitionException($"Saga '{SagaType}' never declared an InitialState(...).");

    public IReadOnlyCollection<Type> MessageTypes =>
        _model.StepsByState.Values.SelectMany(byType => byType.Keys).Distinct().ToArray();

    public IReadOnlyCollection<Type> InitiatingMessageTypes =>
        _model.StepsByState.TryGetValue(InitialStateName, out var steps) ? steps.Keys.ToArray() : [];

    public bool CanInitiate(Type messageType) => InitiatingMessageTypes.Contains(messageType);

    /// <summary>Declares the state a brand new saga instance starts in.</summary>
    protected State<TState> InitialState(string name)
    {
        if (_model.InitialStateName is not null)
            throw new SagaDefinitionException($"Saga '{SagaType}' already declared InitialState '{_model.InitialStateName}'.");

        _model.InitialStateName = name;
        return new State<TState>(name);
    }

    protected State<TState> State(string name) => new(name);

    protected StateBuilder<TState> During(params State<TState>[] states) =>
        new(_model, states.Select(s => s.Name).ToArray());

    /// <summary>Registers compensation for a state that ran; invoked (most-recently-visited first) by <c>.Compensate()</c>.</summary>
    protected void Compensate(State<TState> forState, Func<ISagaContext<TState>, CancellationToken, Task> action) =>
        _model.Compensations[forState.Name] = action;

    protected void WithTimeout(State<TState> state, TimeSpan after, Action<TimeoutBuilder<TState>> configure)
    {
        var builder = new TimeoutBuilder<TState>();
        configure(builder);
        _model.Timeouts[state.Name] = (after, builder.Step);
    }

    protected void OnUnhandledEvent(UnhandledEventPolicy policy) => _model.UnhandledEventPolicy = policy;

    public TimeSpan? GetTimeout(string forState) =>
        _model.Timeouts.TryGetValue(forState, out var timeout) ? timeout.Delay : null;

    public async Task<SagaStepOutcome> HandleAsync(ISagaContext<TState> context, object message, CancellationToken cancellationToken)
    {
        var fromState = context.Saga.CurrentState;
        var messageType = message.GetType();

        if (!_model.StepsByState.TryGetValue(fromState, out var steps) || !steps.TryGetValue(messageType, out var step))
        {
            if (_model.UnhandledEventPolicy == UnhandledEventPolicy.Throw)
                throw new InvalidOperationException($"Saga '{SagaType}' has no handler for {messageType.Name} while in state '{fromState}'.");

            return SagaStepOutcome.Unhandled(fromState);
        }

        await ExecuteStepAsync(step, context, message, cancellationToken);

        var toState = step.TargetStateName ?? fromState;
        context.Saga.CurrentState = toState;
        if (step.FinalStatus is { } finalStatus)
            context.Saga.Status = finalStatus;

        return new SagaStepOutcome(true, fromState, toState, step.FinalStatus);
    }

    public async Task<SagaStepOutcome> HandleTimeoutAsync(ISagaContext<TState> context, string forState, CancellationToken cancellationToken)
    {
        if (!_model.Timeouts.TryGetValue(forState, out var timeout))
            return SagaStepOutcome.Unhandled(forState);

        await ExecuteStepAsync(timeout.Step, context, TimeoutSignal.Instance, cancellationToken);

        var toState = timeout.Step.TargetStateName ?? forState;
        context.Saga.CurrentState = toState;
        if (timeout.Step.FinalStatus is { } finalStatus)
            context.Saga.Status = finalStatus;

        return new SagaStepOutcome(true, forState, toState, timeout.Step.FinalStatus);
    }

    private static async Task ExecuteStepAsync(StepDefinition<TState> step, ISagaContext<TState> context, object message, CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                foreach (var action in step.Actions)
                    await action(context, message);

                return;
            }
            catch when (attempt < step.RetryPolicy.MaxAttempts)
            {
                BugsMqDiagnostics.StepRetries.Add(1, new KeyValuePair<string, object?>(BugsMqDiagnostics.TagSagaType, context.Saga.SagaType));
                await Task.Delay(step.RetryPolicy.DelayForAttempt(attempt), cancellationToken);
            }
        }
    }
}
