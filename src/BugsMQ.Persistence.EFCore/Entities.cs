using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Persistence.EFCore;

/// <summary>
/// Current-state snapshot row, shared by every saga type — the business-specific fields live in
/// <see cref="DataJson"/> (the serialized TState), so one table works for any number of saga types.
/// </summary>
public sealed class SagaInstanceEntity
{
    public Guid CorrelationId { get; set; }

    public string SagaType { get; set; } = string.Empty;

    public SagaKind Kind { get; set; }

    public string CurrentState { get; set; } = string.Empty;

    public SagaStatus Status { get; set; }

    public int Version { get; set; }

    public string DataJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Append-only audit-trail row. <see cref="Id"/> (identity/serial) doubles as the per-correlation-id ordering sequence number.</summary>
public sealed class SagaEventLogEntity
{
    public long Id { get; set; }

    public Guid CorrelationId { get; set; }

    public string SagaType { get; set; } = string.Empty;

    public SagaEntryType EntryType { get; set; }

    public string? FromState { get; set; }

    public string? ToState { get; set; }

    public string? MessageType { get; set; }

    public string? MessageId { get; set; }

    public string? PayloadJson { get; set; }

    public string? ErrorMessage { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class SagaTimeoutEntity
{
    public long Id { get; set; }

    public Guid CorrelationId { get; set; }

    public string SagaType { get; set; } = string.Empty;

    public string ForState { get; set; } = string.Empty;

    public DateTimeOffset DueAtUtc { get; set; }

    public SagaTimeoutStatus Status { get; set; }
}
