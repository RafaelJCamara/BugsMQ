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
    /// to address from its own <c>Saga.ParentCorrelationId</c>.
    /// </para>
    /// </summary>
    Task StartChildAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull;
}
