using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Abstractions.Persistence;

/// <summary>
/// Narrow administrative write, separate from <see cref="ISagaSnapshotStore{TState}"/>: resets a
/// saga's CurrentState/Status columns directly without touching its business DataJson or needing to
/// know the concrete TState type. Used by the dashboard's whole-saga retry when a Failed saga has no
/// specific technical step failure to redrive (e.g. it reached Failed via a normal business
/// transition or a timeout) — the saga is reset to an earlier state and the message that produced
/// that state is replayed.
/// </summary>
public interface ISagaAdminStore
{
    Task ResetStateAsync(string sagaType, Guid correlationId, string currentState, SagaStatus status, CancellationToken cancellationToken = default);
}
