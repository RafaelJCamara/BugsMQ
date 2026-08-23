using System.Linq.Expressions;
using System.Reflection;
using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Core.Dsl;

/// <summary>
/// Fluent configuration for one (state, message type) step. Inherits <c>When&lt;T&gt;()</c> from
/// <see cref="StateBuilder{TState}"/> so a chain can move straight on to the next event without
/// re-stating <c>During(...)</c>.
/// </summary>
public sealed class EventBuilder<TState, TMessage> : StateBuilder<TState>
    where TState : SagaState, new()
    where TMessage : notnull
{
    private readonly StepDefinition<TState> _step;

    internal EventBuilder(SagaDefinitionModel<TState> model, IReadOnlyList<string> stateNames, StepDefinition<TState> step)
        : base(model, stateNames)
    {
        _step = step;
    }

    /// <summary>
    /// Extracts a business key from the initiating message and stores it on the newly created saga
    /// state, for dashboard search/traceability. Correlation of subsequent messages to this saga
    /// instance is done by the transport-stamped correlation id, not by this business key.
    /// </summary>
    public EventBuilder<TState, TMessage> CorrelateBy<TKey>(Func<TMessage, TKey> messageKey, Expression<Func<TState, TKey>> stateKey)
    {
        if (stateKey.Body is not MemberExpression { Member: PropertyInfo property })
            throw new SagaDefinitionException($"CorrelateBy state selector for {typeof(TState).Name} must be a simple property access, e.g. s => s.OrderId.");

        _step.Actions.Insert(0, (ctx, msg) =>
        {
            property.SetValue(ctx.Saga, messageKey((TMessage)msg));
            return Task.CompletedTask;
        });

        return this;
    }

    public EventBuilder<TState, TMessage> Then(Action<ISagaContext<TState>, TMessage> action)
    {
        _step.Actions.Add((ctx, msg) =>
        {
            action(ctx, (TMessage)msg);
            return Task.CompletedTask;
        });

        return this;
    }

    public EventBuilder<TState, TMessage> Then(Func<ISagaContext<TState>, TMessage, Task> action)
    {
        _step.Actions.Add((ctx, msg) => action(ctx, (TMessage)msg));
        return this;
    }

    public EventBuilder<TState, TMessage> Publish<TOut>(Func<ISagaContext<TState>, TMessage, TOut> factory) where TOut : notnull
    {
        _step.Actions.Add((ctx, msg) => ctx.PublishAsync(factory(ctx, (TMessage)msg), ctx.CancellationToken));
        return this;
    }

    public EventBuilder<TState, TMessage> Send<TOut>(string destination, Func<ISagaContext<TState>, TMessage, TOut> factory) where TOut : notnull
    {
        _step.Actions.Add((ctx, msg) => ctx.SendAsync(destination, factory(ctx, (TMessage)msg), ctx.CancellationToken));
        return this;
    }

    /// <summary>Bounded, in-process retry applied to this step's actions as a whole if they throw.</summary>
    public EventBuilder<TState, TMessage> Retry(RetryPolicy policy)
    {
        _step.RetryPolicy = policy;
        return this;
    }

    public EventBuilder<TState, TMessage> TransitionTo(State<TState> state)
    {
        _step.TargetStateName = state.Name;
        return this;
    }

    /// <summary>Marks the saga terminal with the given status once this step completes.</summary>
    public EventBuilder<TState, TMessage> Finalize(SagaStatus status)
    {
        _step.FinalStatus = status;
        return this;
    }

    /// <summary>
    /// Runs compensation for every state this saga instance has visited (most-recent first) that has
    /// a registered <c>Compensate(state, ...)</c> action. Typically used on the step handling a
    /// downstream failure event, e.g. <c>.When&lt;PaymentFailed&gt;().Compensate().TransitionTo(Failed)</c>.
    /// </summary>
    public EventBuilder<TState, TMessage> Compensate()
    {
        _step.Actions.Add((ctx, _) => Model.RunCompensationAsync(ctx, ctx.VisitedStates.Reverse(), ctx.CancellationToken));
        return this;
    }
}
