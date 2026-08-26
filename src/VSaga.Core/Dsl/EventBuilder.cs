using System.Linq.Expressions;
using System.Reflection;
using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

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
        _step.TargetStateSelector = null;
        return this;
    }

    /// <summary>
    /// Chooses the next state from the saga's accumulated state, evaluated after this step's actions
    /// have run so it sees what they just wrote.
    /// <para>
    /// This is the join half of a parallel fan-out. Dispatching branches in parallel already worked —
    /// <c>.Publish(...)</c> chains, so one step can send several commands at once — but the replies
    /// then arrive in an order nobody controls, and the fixed <c>TransitionTo(state)</c> could only move
    /// on unconditionally. Returning the gathering state keeps the saga waiting; returning the next
    /// state releases it. Register the same selector on every branch and whichever reply happens to be
    /// last is the one that advances the saga, without any branch assuming it is last:
    /// </para>
    /// <code>
    /// During(Gathering)
    ///     .When&lt;InventoryReserved&gt;()
    ///         .Then((ctx, _) =&gt; ctx.Saga.InventoryDone = true)
    ///         .TransitionTo(s =&gt; s.AllBranchesDone ? ReadyToShip : Gathering)
    ///     .When&lt;PaymentCharged&gt;()
    ///         .Then((ctx, _) =&gt; ctx.Saga.PaymentDone = true)
    ///         .TransitionTo(s =&gt; s.AllBranchesDone ? ReadyToShip : Gathering);
    /// </code>
    /// <para>
    /// Returning the gathering state is a self-transition, which the orchestrator treats as "no
    /// transition": it does not cancel or reschedule that state's timeout. That is the behaviour a join
    /// wants — one deadline covers the whole gather, rather than each arriving branch silently
    /// extending it — but it does mean a branch cannot carry its own separate deadline.
    /// </para>
    /// </summary>
    public EventBuilder<TState, TMessage> TransitionTo(Func<TState, State<TState>> selector)
    {
        _step.TargetStateSelector = state => selector(state).Name;
        _step.TargetStateName = null;
        return this;
    }

    /// <summary>Marks the saga terminal with the given status once this step completes.</summary>
    public EventBuilder<TState, TMessage> Finalize(SagaStatus status)
    {
        _step.FinalStatus = status;
        _step.FinalStatusSelector = null;
        return this;
    }

    /// <summary>
    /// Terminal-or-not decided from the saga's accumulated state, evaluated after this step's actions
    /// have run. Return null for "handled, but not terminal yet".
    /// <para>
    /// Needed for a fan-out join whose completion *is* the saga's ending. Ordinarily an orchestrated
    /// saga expresses a conditional ending by gating on state — put the ending in its own
    /// <c>During(...)</c> — but that does not reach the case where the last branch to arrive must both
    /// release the join and finish the saga in one step, because no branch knows it is last. A fixed
    /// <c>Finalize(status)</c> on each branch would complete the saga on the first reply.
    /// </para>
    /// </summary>
    public EventBuilder<TState, TMessage> Finalize(Func<TState, SagaStatus?> selector)
    {
        _step.FinalStatusSelector = selector;
        _step.FinalStatus = null;
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
