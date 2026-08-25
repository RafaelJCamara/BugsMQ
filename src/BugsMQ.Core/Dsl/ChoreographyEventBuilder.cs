using System.Linq.Expressions;
using System.Reflection;
using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Core.Dsl;

/// <summary>
/// Fluent configuration for one event a <see cref="ChoreographedSagaDefinition{TState}"/> reacts to.
/// Returned by <c>On&lt;TMessage&gt;()</c>. Deliberately does not chain into a further <c>.On&lt;T&gt;()</c>
/// the way <see cref="EventBuilder{TState,TMessage}"/> chains after <c>During(...)</c> — there is no
/// shared "state" context to carry between events in a choreography, so each reaction is configured as
/// its own independent statement.
/// </summary>
public sealed class ChoreographyEventBuilder<TState, TMessage>
    where TState : SagaState, new()
    where TMessage : notnull
{
    private readonly ChoreographySagaModel<TState> _model;
    private readonly StepDefinition<TState> _step;

    internal ChoreographyEventBuilder(ChoreographySagaModel<TState> model, StepDefinition<TState> step)
    {
        _model = model;
        _step = step;
    }

    /// <summary>
    /// Marks <typeparamref name="TMessage"/> as an event that can create a brand new tracked instance
    /// when observed with no existing saga for its correlation id — a choreography can have more than
    /// one such event, since (unlike orchestration's single <c>InitialState</c>) there is no designated
    /// first step: whichever independent participant happens to publish first is the one that starts it.
    /// </summary>
    public ChoreographyEventBuilder<TState, TMessage> StartsNewInstance()
    {
        _model.InitiatingMessageTypes.Add(typeof(TMessage));
        return this;
    }

    /// <summary>
    /// Extracts a business key from the observed message and stores it on the saga state, for dashboard
    /// search/traceability. Correlation of subsequent events to this saga instance is done by the
    /// transport-stamped correlation id, not by this business key.
    /// </summary>
    public ChoreographyEventBuilder<TState, TMessage> CorrelateBy<TKey>(Func<TMessage, TKey> messageKey, Expression<Func<TState, TKey>> stateKey)
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

    public ChoreographyEventBuilder<TState, TMessage> Then(Action<ISagaContext<TState>, TMessage> action)
    {
        _step.Actions.Add((ctx, msg) =>
        {
            action(ctx, (TMessage)msg);
            return Task.CompletedTask;
        });

        return this;
    }

    public ChoreographyEventBuilder<TState, TMessage> Then(Func<ISagaContext<TState>, TMessage, Task> action)
    {
        _step.Actions.Add((ctx, msg) => action(ctx, (TMessage)msg));
        return this;
    }

    public ChoreographyEventBuilder<TState, TMessage> Publish<TOut>(Func<ISagaContext<TState>, TMessage, TOut> factory) where TOut : notnull
    {
        _step.Actions.Add((ctx, msg) => ctx.PublishAsync(factory(ctx, (TMessage)msg), ctx.CancellationToken));
        return this;
    }

    public ChoreographyEventBuilder<TState, TMessage> Send<TOut>(string destination, Func<ISagaContext<TState>, TMessage, TOut> factory) where TOut : notnull
    {
        _step.Actions.Add((ctx, msg) => ctx.SendAsync(destination, factory(ctx, (TMessage)msg), ctx.CancellationToken));
        return this;
    }

    /// <summary>Bounded, in-process retry applied to this step's actions as a whole if they throw.</summary>
    public ChoreographyEventBuilder<TState, TMessage> Retry(RetryPolicy policy)
    {
        _step.RetryPolicy = policy;
        return this;
    }

    /// <summary>
    /// Records this event as the saga's latest observed milestone — a label for the dashboard/timeline
    /// and for keying <c>Compensate(...)</c>/<c>WithTimeout(...)</c>, not a gate: unlike orchestration's
    /// <c>TransitionTo</c>, nothing about the choreography's own dispatch depends on this value.
    /// </summary>
    public ChoreographyEventBuilder<TState, TMessage> RecordState(State<TState> state)
    {
        _step.TargetStateName = state.Name;
        return this;
    }

    /// <summary>Marks the saga terminal with the given status once this step completes.</summary>
    public ChoreographyEventBuilder<TState, TMessage> Finalize(SagaStatus status)
    {
        _step.FinalStatus = status;
        return this;
    }

    /// <summary>
    /// Runs compensation for every state this saga instance has visited (most-recent first) that has a
    /// registered <c>Compensate(state, ...)</c> action. Typically used on the step handling a downstream
    /// failure event, e.g. <c>.On&lt;PaymentFailed&gt;().Compensate().RecordState(Failed)</c>.
    /// </summary>
    public ChoreographyEventBuilder<TState, TMessage> Compensate()
    {
        _step.Actions.Add((ctx, _) => _model.RunCompensationAsync(ctx, ctx.VisitedStates.Reverse(), ctx.CancellationToken));
        return this;
    }
}
