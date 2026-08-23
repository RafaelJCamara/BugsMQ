using System.Text.Json;
using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using Microsoft.EntityFrameworkCore;

namespace BugsMQ.Persistence.EFCore;

public sealed class EfCoreSagaSnapshotStore<TState>(BugsMqDbContext db) : ISagaSnapshotStore<TState>
    where TState : SagaState
{
    public async Task<TState?> FindAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var entity = await db.SagaInstances.AsNoTracking().FirstOrDefaultAsync(x => x.CorrelationId == correlationId, cancellationToken);
        return entity is null ? null : JsonSerializer.Deserialize<TState>(entity.DataJson);
    }

    public async Task InsertAsync(TState state, CancellationToken cancellationToken = default)
    {
        var entity = ToEntity(state);
        db.SagaInstances.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new SagaAlreadyExistsException(state.CorrelationId);
        }
    }

    public async Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default)
    {
        var entity = await db.SagaInstances.FirstOrDefaultAsync(x => x.CorrelationId == state.CorrelationId, cancellationToken)
                     ?? throw new SagaNotFoundException(state.CorrelationId);

        if (entity.Version != expectedVersion)
            throw new SagaConcurrencyException(state.CorrelationId, expectedVersion);

        // Bump state.Version BEFORE serializing DataJson from it — otherwise the JSON blob embeds
        // the stale version even though the entity's own Version column is correct, and FindAsync
        // (which deserializes from DataJson) would silently return the old version.
        state.Version = expectedVersion + 1;
        var updated = ToEntity(state);
        entity.SagaType = updated.SagaType;
        entity.Kind = updated.Kind;
        entity.CurrentState = updated.CurrentState;
        entity.Status = updated.Status;
        entity.DataJson = updated.DataJson;
        entity.UpdatedAtUtc = updated.UpdatedAtUtc;
        entity.Version = updated.Version;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            state.Version = expectedVersion;
            throw new SagaConcurrencyException(state.CorrelationId, expectedVersion);
        }
    }

    private static SagaInstanceEntity ToEntity(TState state) => new()
    {
        CorrelationId = state.CorrelationId,
        SagaType = state.SagaType,
        Kind = state.Kind,
        CurrentState = state.CurrentState,
        Status = state.Status,
        Version = state.Version,
        DataJson = JsonSerializer.Serialize(state),
        CreatedAtUtc = state.CreatedAtUtc,
        UpdatedAtUtc = state.UpdatedAtUtc,
    };
}
