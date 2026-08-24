using BugsMQ.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BugsMQ.Persistence.EFCore;

public sealed class EfCoreSagaEventLogStore(BugsMqDbContext db) : ISagaEventLogStore
{
    public async Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        var entity = new SagaEventLogEntity
        {
            CorrelationId = entry.CorrelationId,
            SagaType = entry.SagaType,
            EntryType = entry.EntryType,
            FromState = entry.FromState,
            ToState = entry.ToState,
            MessageType = entry.MessageType,
            MessageId = entry.MessageId,
            PayloadJson = entry.PayloadJson,
            ErrorMessage = entry.ErrorMessage,
            TraceId = entry.TraceId,
            SpanId = entry.SpanId,
            OccurredAtUtc = entry.OccurredAtUtc,
        };

        db.SagaEventLog.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        var entities = await db.SagaEventLog.AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(ToLogEntry).ToList();
    }

    public Task<bool> IsDuplicateAsync(Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
        db.SagaEventLog.AsNoTracking().AnyAsync(x => x.CorrelationId == correlationId && x.MessageId == messageId, cancellationToken);

    private static SagaLogEntry ToLogEntry(SagaEventLogEntity e) =>
        new(e.Id, e.CorrelationId, e.SagaType, e.EntryType, e.FromState, e.ToState, e.MessageType, e.MessageId,
            e.PayloadJson, e.ErrorMessage, e.TraceId, e.SpanId, e.OccurredAtUtc);
}
