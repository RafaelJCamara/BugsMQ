using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Persistence;

/// <summary>
/// Current-state snapshot per saga instance, typed to the saga's own state class. Used by the
/// orchestrator for fast load/save with optimistic concurrency. Separate from the append-only
/// event log (<see cref="ISagaEventLogStore"/>), which is the audit/timeline/redrive source of truth.
/// </summary>
/// <remarks>
/// A saga instance is identified by <c>(sagaType, correlationId)</c>, not by correlation id alone:
/// two different saga types may legitimately track the same business correlation id — that is exactly
/// what lets a choreographed saga observe messages already flowing under an orchestrated saga's id.
/// <typeparamref name="TState"/> does not imply the saga type (two saga definitions can share a state
/// class), so the type is always passed explicitly rather than inferred.
/// </remarks>
public interface ISagaSnapshotStore<TState> where TState : SagaState
{
    Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="SagaAlreadyExistsException"/> if a snapshot already exists for this state's own (SagaType, CorrelationId) pair.</summary>
    Task InsertAsync(TState state, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="SagaConcurrencyException"/> if the stored version does not equal <paramref name="expectedVersion"/>.</summary>
    Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up the instance that reserved this business key, or null if none has. At most one instance
    /// can exist for a given (sagaType, businessKey) pair -- enforced at InsertAsync time by a unique
    /// constraint, not by this method.
    /// </summary>
    Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default);
}
