namespace BugsMQ.Abstractions.Persistence;

/// <summary>One append-only audit-trail row. The full ordered set per correlation id is the saga's timeline.</summary>
public sealed record SagaLogEntry(
    long SequenceNumber,
    Guid CorrelationId,
    string SagaType,
    SagaEntryType EntryType,
    string? FromState,
    string? ToState,
    string? MessageType,
    string? MessageId,
    string? PayloadJson,
    string? ErrorMessage,
    string? TraceId,
    string? SpanId,
    DateTimeOffset OccurredAtUtc,
    // Appended after OccurredAtUtc, each defaulted, so every existing positional `new SagaLogEntry(...)`
    // call site (notably EfCoreSagaEventLogStore.ToLogEntry) keeps compiling unchanged.
    string? SourceService = null,
    string? DestinationService = null,
    string? CausationId = null)
{
    public static SagaLogEntry Create(
        Guid correlationId,
        string sagaType,
        SagaEntryType entryType,
        string? fromState = null,
        string? toState = null,
        string? messageType = null,
        string? messageId = null,
        string? payloadJson = null,
        string? errorMessage = null,
        string? traceId = null,
        string? spanId = null,
        DateTimeOffset? occurredAtUtc = null,
        string? sourceService = null,
        string? destinationService = null,
        string? causationId = null) =>
        new(0, correlationId, sagaType, entryType, fromState, toState, messageType, messageId,
            payloadJson, errorMessage, traceId, spanId, occurredAtUtc ?? DateTimeOffset.UtcNow,
            sourceService, destinationService, causationId);
}
