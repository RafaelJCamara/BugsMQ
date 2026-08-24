using System.Text.Json.Nodes;
using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using Microsoft.EntityFrameworkCore;

namespace BugsMQ.Persistence.EFCore;

public sealed class EfCoreSagaSummaryReader(BugsMqDbContext db) : ISagaSummaryReader, ISagaAdminStore
{
    public async Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
    {
        var query = db.SagaInstances.AsNoTracking().AsQueryable();

        if (filter.Status is { } status)
            query = query.Where(x => x.Status == status);
        if (filter.Kind is { } kind)
            query = query.Where(x => x.Kind == kind);
        if (!string.IsNullOrWhiteSpace(filter.SagaType))
            query = query.Where(x => x.SagaType == filter.SagaType);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x => EF.Functions.Like(x.SagaType, $"%{search}%") || x.CorrelationId.ToString().Contains(search));
        }

        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Max(filter.PageSize, 1);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new SagaSummary(x.CorrelationId, x.SagaType, x.Kind, x.CurrentState, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<SagaSummary>(items, page, pageSize, totalCount);
    }

    public Task<SagaSummary?> GetAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        return db.SagaInstances.AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .Select(x => new SagaSummary(x.CorrelationId, x.SagaType, x.Kind, x.CurrentState, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.Version))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<string?> GetDataJsonAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
        db.SagaInstances.AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .Select(x => x.DataJson)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default)
    {
        // Project into an anonymous type first: Npgsql's provider can't translate Distinct() over a
        // Select() projecting straight into SagaTypeInfo's record constructor. Anonymous types (and
        // Distinct/OrderBy over them) translate to plain `SELECT DISTINCT` fine; map to the public
        // record client-side afterward.
        var rows = await db.SagaInstances.AsNoTracking()
            .Select(x => new { x.SagaType, x.Kind })
            .Distinct()
            .OrderBy(t => t.SagaType)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new SagaTypeInfo(r.SagaType, r.Kind)).ToList();
    }

    public async Task ResetStateAsync(Guid correlationId, string currentState, SagaStatus status, CancellationToken cancellationToken = default)
    {
        var entity = await db.SagaInstances.FirstOrDefaultAsync(x => x.CorrelationId == correlationId, cancellationToken)
                     ?? throw new SagaNotFoundException(correlationId);

        // DataJson embeds its own CurrentState/Status (it's the full serialized TState) — patch by
        // property name rather than deserializing into a concrete TState (unknown here), same fix as
        // EfCoreSagaSnapshotStore's Version bug: the entity columns and DataJson must never disagree,
        // since FindAsync<TState> reads CurrentState/Status back out of DataJson, not the columns.
        var newVersion = entity.Version + 1;
        var node = JsonNode.Parse(entity.DataJson)!.AsObject();
        node["CurrentState"] = currentState;
        node["Status"] = (int)status;
        node["Version"] = newVersion;

        entity.DataJson = node.ToJsonString();
        entity.CurrentState = currentState;
        entity.Status = status;
        entity.Version = newVersion;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
    }
}
