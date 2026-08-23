namespace BugsMQ.Abstractions.Persistence;

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
public interface ISagaTimeoutStore
{
    Task ScheduleAsync(Guid correlationId, string sagaType, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Cancels any pending timeout for this saga/state pair (called when the saga transitions away before it fires).</summary>
    Task CancelAsync(Guid correlationId, string forState, CancellationToken cancellationToken = default);

    /// <summary>Atomically claims (marks Fired) and returns up to <paramref name="batchSize"/> due timeouts, for the dispatcher to act on.</summary>
    Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, CancellationToken cancellationToken = default);
}
