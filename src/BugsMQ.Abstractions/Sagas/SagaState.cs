namespace BugsMQ.Abstractions.Sagas;

/// <summary>
/// Base type for a saga instance's persisted data. Concrete sagas derive from this to add
/// their own business-specific fields (order id, amount, etc).
/// </summary>
public abstract class SagaState
{
    public Guid CorrelationId { get; set; }

    public string SagaType { get; set; } = string.Empty;

    public SagaKind Kind { get; set; } = SagaKind.Orchestrated;

    public string CurrentState { get; set; } = string.Empty;

    public SagaStatus Status { get; set; } = SagaStatus.Running;

    /// <summary>Optimistic concurrency token, incremented on every persisted transition.</summary>
    public int Version { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
