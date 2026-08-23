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
}
