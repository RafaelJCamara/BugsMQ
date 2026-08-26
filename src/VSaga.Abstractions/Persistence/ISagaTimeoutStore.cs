namespace VSaga.Abstractions.Persistence;

public enum SagaTimeoutStatus
{
    Pending,
    Fired,
    Cancelled,
}

public sealed record SagaTimeout(
    long Id,
    Guid CorrelationId,
    string SagaType,
    string ForState,
    DateTimeOffset DueAtUtc,
    SagaTimeoutStatus Status);

/// <summary>Durable schedule for state-timeout transitions, polled by the SagaTimeoutDispatcher hosted service.</summary>
/// <remarks>
/// Scoped per saga instance — <c>(sagaType, correlationId)</c>. State names are only unique within a
/// saga type, so two saga types sharing a correlation id can each have a pending timeout for a
/// same-named state; cancelling must not reach across into the other's.
/// </remarks>
public interface ISagaTimeoutStore
{
    Task ScheduleAsync(string sagaType, Guid correlationId, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Cancels any pending timeout for this saga instance/state pair (called when the saga transitions away before it fires).</summary>
    Task CancelAsync(string sagaType, Guid correlationId, string forState, CancellationToken cancellationToken = default);

    /// <summary>Atomically claims (marks Fired) and returns up to <paramref name="batchSize"/> due timeouts, for the dispatcher to act on.</summary>
    Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, CancellationToken cancellationToken = default);
}
