namespace BugsMQ.Abstractions.Persistence;

/// <summary>Append-only audit trail: powers the dashboard timeline, precise manual-retry redrive, and compensation replay.</summary>
/// <remarks>
/// Reads are scoped to one saga instance — <c>(sagaType, correlationId)</c> — not to the correlation
/// id alone. Two saga types tracking the same correlation id each keep their own independent timeline;
/// merging them would corrupt both the dashboard view and, more seriously,
/// <c>SagaOrchestrator.GetVisitedStatesAsync</c>, which derives the compensation set from this log.
/// <see cref="AppendAsync"/> needs no separate parameter — every <see cref="SagaLogEntry"/> already
/// carries its own <see cref="SagaLogEntry.SagaType"/>.
/// </remarks>
public interface ISagaEventLogStore
{
    /// <summary>Appends the entry and returns its assigned, per-saga-instance-ordered sequence number.</summary>
    Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>True if an entry with this (sagaType, correlationId, messageId) was already recorded — the idempotency/dedupe check.</summary>
    Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default);
}
