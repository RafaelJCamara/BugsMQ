using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Core.Dsl;

/// <summary>Configures what happens when a state's timeout fires — mirrors the step-configuration subset of <see cref="EventBuilder{TState,TMessage}"/>.</summary>
public sealed class TimeoutBuilder<TState> where TState : SagaState, new()
{
    private readonly SagaDefinitionModel<TState> _model;

    internal readonly StepDefinition<TState> Step = new(typeof(TimeoutSignal));

    internal TimeoutBuilder(SagaDefinitionModel<TState> model)
    {
        _model = model;
    }

    public TimeoutBuilder<TState> Then(Action<ISagaContext<TState>> action)
    {
        Step.Actions.Add((ctx, _) =>
        {
            action(ctx);
            return Task.CompletedTask;
        });

        return this;
    }

    public TimeoutBuilder<TState> Publish<TOut>(Func<ISagaContext<TState>, TOut> factory) where TOut : notnull
    {
        Step.Actions.Add((ctx, _) => ctx.PublishAsync(factory(ctx), ctx.CancellationToken));
        return this;
    }

    public TimeoutBuilder<TState> TransitionTo(State<TState> state)
    {
        Step.TargetStateName = state.Name;
        return this;
    }

    public TimeoutBuilder<TState> Finalize(SagaStatus status)
    {
        Step.FinalStatus = status;
        return this;
    }

    /// <summary>Runs compensation for every visited state with a registered Compensate(state, ...) action, most-recent first — see EventBuilder.Compensate().</summary>
    public TimeoutBuilder<TState> Compensate()
    {
        Step.Actions.Add((ctx, _) => _model.RunCompensationAsync(ctx, ctx.VisitedStates.Reverse(), ctx.CancellationToken));
        return this;
    }
}
