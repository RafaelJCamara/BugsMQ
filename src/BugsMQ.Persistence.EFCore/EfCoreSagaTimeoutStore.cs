using BugsMQ.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BugsMQ.Persistence.EFCore;

public sealed class EfCoreSagaTimeoutStore(BugsMqDbContext db) : ISagaTimeoutStore
{
    public async Task ScheduleAsync(Guid correlationId, string sagaType, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default)
    {
        db.SagaTimeouts.Add(new SagaTimeoutEntity
        {
            CorrelationId = correlationId,
            SagaType = sagaType,
            ForState = forState,
            DueAtUtc = dueAtUtc,
            Status = SagaTimeoutStatus.Pending,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid correlationId, string forState, CancellationToken cancellationToken = default)
    {
        var pending = await db.SagaTimeouts
            .Where(x => x.CorrelationId == correlationId && x.ForState == forState && x.Status == SagaTimeoutStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var timeout in pending)
            timeout.Status = SagaTimeoutStatus.Cancelled;

        if (pending.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    /// <remarks>
    /// Claims by loading due rows and marking them Fired in one SaveChanges — correct for a single
    /// dispatcher instance. Running multiple BugsMQ.Dashboard.Api/worker instances concurrently could
    /// race on the same row; a production-hardened version would use a provider-specific atomic
    /// UPDATE...RETURNING/OUTPUT claim. Out of scope for v1.
    /// </remarks>
    public async Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, CancellationToken cancellationToken = default)
    {
        var due = await db.SagaTimeouts
            .Where(x => x.Status == SagaTimeoutStatus.Pending && x.DueAtUtc <= asOf)
            .OrderBy(x => x.DueAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
            return [];

        foreach (var timeout in due)
            timeout.Status = SagaTimeoutStatus.Fired;

        await db.SaveChangesAsync(cancellationToken);

        return due.Select(x => new SagaTimeout(x.Id, x.CorrelationId, x.SagaType, x.ForState, x.DueAtUtc, x.Status)).ToList();
    }
}
