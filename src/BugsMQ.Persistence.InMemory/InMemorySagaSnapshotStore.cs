using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Persistence.InMemory;

/// <summary>Thin per-TState wrapper over the shared <see cref="InMemorySagaStore"/> singleton.</summary>
public sealed class InMemorySagaSnapshotStore<TState>(InMemorySagaStore store) : ISagaSnapshotStore<TState>
    where TState : SagaState
{
    public Task<TState?> FindAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(store.Find<TState>(correlationId));

    public Task InsertAsync(TState state, CancellationToken cancellationToken = default)
    {
        store.Insert(state);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
    {
        store.Update(state, expectedVersion);
        return Task.CompletedTask;
    }
}
