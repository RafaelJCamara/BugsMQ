namespace BugsMQ.Abstractions.Sagas;

/// <summary>
/// Runtime context handed to a saga's step actions (Then/Publish/Compensate delegates).
/// Gives access to the mutable saga state, correlation, and outbound messaging.
/// </summary>
public interface ISagaContext<out TState> where TState : SagaState
{
    TState Saga { get; }

    Guid CorrelationId { get; }

    /// <summary>States this saga instance has successfully transitioned into so far, oldest first
    /// (derived from the persisted event log). Used by <c>.Compensate()</c> to walk backward through
    /// only the states that actually completed and had a compensation registered.</summary>
    IReadOnlyList<string> VisitedStates { get; }

    IReadOnlyDictionary<string, string> Headers { get; }

    IServiceProvider Services { get; }

    CancellationToken CancellationToken { get; }

    Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull;

    Task SendAsync<TMessage>(string destination, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull;

    /// <summary>
    /// Starts another saga as a step of this one: publishes <paramref name="message"/> under a
    /// <b>fresh</b> correlation id, stamped with this instance's identity so whichever saga initiates on
    /// it records this one as its parent.
    /// <para>
    /// The parent needs neither the child's state type nor its definition — this is a publish, and
    /// whichever saga's <c>CanInitiate</c> matches becomes the child, exactly like the dashboard's retry
    /// redrive. Two consequences worth knowing before using it: if no saga initiates on that message
    /// type, no child is ever created and nobody is told; if two do, two children start and the parent
    /// has no way to tell.
    /// </para>
    /// <para>
    /// This does not wait. The parent moves on as soon as the publish returns, so waiting for a child is
    /// the ordinary join — park in a state and let the child's own message release it:
    /// <c>.TransitionTo(s =&gt; s.ChildDone ? Ready : AwaitingChild)</c>. The child learns which instance
    /// to address from its own <c>Saga.ParentCorrelationId</c>, via <see cref="NotifyParentAsync{TMessage}"/>.
    /// </para>
    /// </summary>
    Task StartChildAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull;

    /// <summary>
    /// The other half of a wait: publishes <paramref name="message"/> under <c>Saga.ParentCorrelationId</c>
    /// so a parent parked via the join above sees it as an ordinary inbound message on its own instance.
    /// Throws <see cref="InvalidOperationException"/> immediately, before publishing anything, if this
    /// saga has no parent — a root saga has nobody to notify.
    /// <para>
    /// Not a general publish-under-any-id overload: the only correlation id this can address is the one
    /// the engine already put on <c>Saga.ParentCorrelationId</c> when it created this instance, so a saga
    /// can never mint an orphan instance under an id it invented. That also means the notification fans
    /// out to <b>every</b> saga type subscribed to <typeparamref name="TMessage"/> that tracks an
    /// instance under the parent's correlation id, not only the one that called
    /// <see cref="StartChildAsync{TMessage}"/> — worth knowing when several saga types share one
    /// correlation id (see the "Saga identity" section of the README).
    /// </para>
    /// <para>
    /// Carries the child's actual result, which is exactly what an engine-published completion event
    /// cannot: a child that fails via an unhandled exception, or simply times out, never reaches this
    /// call — a parent still needs its own timeout on the state it waits in.
    /// </para>
    /// </summary>
    Task NotifyParentAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull;
}
