namespace BugsMQ.Abstractions.Persistence;

/// <summary>Append-only audit trail: powers the dashboard timeline, precise manual-retry redrive, and compensation replay.</summary>
public interface ISagaEventLogStore
{
    /// <summary>Appends the entry and returns its assigned, per-correlation-id-ordered sequence number.</summary>
    Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>True if an entry with this (correlationId, messageId) was already recorded — the idempotency/dedupe check.</summary>
    Task<bool> IsDuplicateAsync(Guid correlationId, string messageId, CancellationToken cancellationToken = default);
}
