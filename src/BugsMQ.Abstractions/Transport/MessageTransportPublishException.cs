namespace BugsMQ.Abstractions.Transport;

/// <summary>
/// Thrown when a transport's publish confirmation reports the broker nacked the message, or returned
/// it as unroutable (e.g. no queue bound for its routing key). Provider-agnostic so callers never need
/// a direct reference to a specific transport's client library.
/// </summary>
public sealed class MessageTransportPublishException(string messageTypeName, Guid correlationId, bool isUnroutable, Exception innerException)
    : Exception(
        $"Publish of '{messageTypeName}' for correlation id '{correlationId}' was {(isUnroutable ? "returned as unroutable" : "nacked")} by the broker.",
        innerException)
{
    public string MessageTypeName { get; } = messageTypeName;

    public Guid CorrelationId { get; } = correlationId;

    /// <summary><c>true</c> if the broker returned the message as unroutable rather than nacking it.</summary>
    public bool IsUnroutable { get; } = isUnroutable;
}
