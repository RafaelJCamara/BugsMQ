namespace VSaga.Abstractions.Persistence;

public enum SagaOutboxStatus
{
    Pending,
    Dispatched,
}

public sealed record SagaOutboxMessage(
    long Id,
    Guid CorrelationId,
    string SagaType,
    string MessageId,
    string MessageTypeName,
    ReadOnlyMemory<byte> Body,
    string? Destination,
    IReadOnlyDictionary<string, string> Headers,
    SagaOutboxStatus Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Durable crash-recovery backstop for <c>ctx.PublishAfterCommitAsync</c>, polled by the
/// SagaOutboxDispatcher hosted service. This is NOT the dispatch path — the inline drain right after
/// each step's own persist still sends every message synchronously, exactly as today. A row here only
/// ever gets (re)dispatched if a crash happens between that persist and the inline drain completing.
/// </summary>
/// <remarks>
/// Scoped per saga instance — <c>(sagaType, correlationId)</c> — the same precedent
/// <see cref="ISagaTimeoutStore"/> establishes.
/// </remarks>
public interface ISagaOutboxStore
{
    /// <summary>
    /// Durably records one queued publish. <paramref name="messageId"/> and <paramref name="createdAtUtc"/>
    /// are supplied by the caller rather than minted here — the row must describe the exact same message
    /// identity as the in-memory dispatch closure it backs, and the caller's own <c>TimeProvider</c> is
    /// what every other timestamp in a saga's timeline already goes through.
    /// </summary>
    Task EnqueueAsync(string sagaType, Guid correlationId, string messageId, string messageTypeName,
        ReadOnlyMemory<byte> body, string? destination, IReadOnlyDictionary<string, string> headers,
        DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one row Dispatched directly, by id — the inline drain's path, called right after it sends
    /// the message synchronously. Never goes through <see cref="ClaimPendingAsync"/>, which is only ever
    /// reached by the recovery poller.
    /// </summary>
    Task MarkDispatchedAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims (marks Dispatched) and returns up to <paramref name="batchSize"/> rows still
    /// Pending and created at or before <paramref name="olderThan"/>, for the recovery poller to
    /// republish. The grace period itself is the caller's concern (<c>now - DispatchGracePeriod</c>) —
    /// this store only ever compares against the absolute cutoff it's given, matching
    /// <see cref="ISagaTimeoutStore.ClaimDueAsync"/>'s own <c>asOf</c> shape.
    /// </summary>
    Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken = default);
}
