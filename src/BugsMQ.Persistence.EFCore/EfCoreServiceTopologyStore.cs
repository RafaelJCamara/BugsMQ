using BugsMQ.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BugsMQ.Persistence.EFCore;

public sealed class EfCoreServiceTopologyStore(BugsMqDbContext db) : IServiceTopologyStore
{
    public async Task RecordAsync(string serviceName, string messageType, string queueName, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default)
    {
        var existing = await db.SagaConsumerRegistrations
            .FirstOrDefaultAsync(x => x.ServiceName == serviceName && x.MessageType == messageType, cancellationToken);

        if (existing is null)
        {
            db.SagaConsumerRegistrations.Add(new SagaConsumerRegistrationEntity
            {
                ServiceName = serviceName,
                MessageType = messageType,
                QueueName = queueName,
                LastSeenAtUtc = seenAtUtc,
            });
        }
        else
        {
            existing.QueueName = queueName;
            existing.LastSeenAtUtc = seenAtUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ServiceTopologyEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Project into an anonymous type first, map to the public record client-side — the same
        // Npgsql-Distinct-over-a-record-projection hazard documented on
        // EfCoreSagaSummaryReader.GetSagaTypesAsync applies to any query shape ending in a record
        // constructor, so this sidesteps it preemptively even without a Distinct() here.
        var rows = await db.SagaConsumerRegistrations.AsNoTracking()
            .Select(x => new { x.ServiceName, x.MessageType, x.QueueName, x.LastSeenAtUtc })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new ServiceTopologyEntry(r.ServiceName, r.MessageType, r.QueueName, r.LastSeenAtUtc)).ToList();
    }
}
