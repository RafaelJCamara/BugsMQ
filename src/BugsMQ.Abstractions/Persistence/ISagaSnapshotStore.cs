using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Abstractions.Persistence;

/// <summary>
/// Current-state snapshot per saga instance, typed to the saga's own state class. Used by the
/// orchestrator for fast load/save with optimistic concurrency. Separate from the append-only
/// event log (<see cref="ISagaEventLogStore"/>), which is the audit/timeline/redrive source of truth.
/// </summary>
public interface ISagaSnapshotStore<TState> where TState : SagaState
{
    Task<TState?> FindAsync(Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="SagaAlreadyExistsException"/> if a snapshot with this correlation id already exists.</summary>
    Task InsertAsync(TState state, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="SagaConcurrencyException"/> if the stored version does not equal <paramref name="expectedVersion"/>.</summary>
    Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default);
}
