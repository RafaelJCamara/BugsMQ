using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Persistence.EFCore;

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

    /// <summary>
    /// The instance that started this one via <c>StartChildAsync</c>, or null/null for a root saga.
    /// Real columns rather than only riding inside <see cref="DataJson"/>: <c>ISagaSummaryReader</c> is
    /// saga-type-agnostic and queries columns, so "which sagas did this one start?" is not answerable
    /// from the blob without deserializing every row.
    /// </summary>
    public string? ParentSagaType { get; set; }

    public Guid? ParentCorrelationId { get; set; }

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

    public string? SourceService { get; set; }

    public string? DestinationService { get; set; }

    public string? CausationId { get; set; }
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

public sealed class SagaOutboxMessageEntity
{
    public long Id { get; set; }

    public Guid CorrelationId { get; set; }

    public string SagaType { get; set; } = string.Empty;

    public string MessageId { get; set; } = string.Empty;

    public string MessageTypeName { get; set; } = string.Empty;

    public byte[] Body { get; set; } = [];

    public string? Destination { get; set; }

    /// <summary>Serialized <c>IReadOnlyDictionary&lt;string, string&gt;</c> envelope headers -- an open dictionary, so a JSON blob rather than a relational shape, same reasoning as <see cref="SagaInstanceEntity.DataJson"/>.</summary>
    public string HeadersJson { get; set; } = "{}";

    public SagaOutboxStatus Status { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>One (service, message type) consumer binding, learned from a real SubscribeAsync call. Composite key gives upsert idempotency for free.</summary>
public sealed class SagaConsumerRegistrationEntity
{
    public string ServiceName { get; set; } = string.Empty;

    public string MessageType { get; set; } = string.Empty;

    public string QueueName { get; set; } = string.Empty;

    public DateTimeOffset LastSeenAtUtc { get; set; }
}
