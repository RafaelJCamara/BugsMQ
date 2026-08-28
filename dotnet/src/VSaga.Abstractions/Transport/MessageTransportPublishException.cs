namespace VSaga.Abstractions.Transport;

/// <summary>
/// Thrown when a transport cannot hand a message off: a broker nack, an unroutable return (e.g. no queue
/// bound for its routing key), or -- for brokerless transports -- a failed delivery to the destination.
/// Provider-agnostic so callers never need a direct reference to a specific transport's client library.
/// </summary>
public sealed class MessageTransportPublishException : Exception
{
    public MessageTransportPublishException(string messageTypeName, Guid correlationId, bool isUnroutable, Exception innerException)
        : this(messageTypeName, correlationId, isUnroutable, detail: null, innerException)
    {
    }

    /// <remarks>
    /// <paramref name="detail"/> is what actually rejected the publish, in the transport's own terms.
    /// Without it the message blames "the broker", which is a false lead in a brokerless transport:
    /// VSaga.Transport.Http reporting a refused TCP connection as a broker nack sends the reader
    /// looking for a RabbitMQ that was never in the picture.
    /// </remarks>
    public MessageTransportPublishException(string messageTypeName, Guid correlationId, bool isUnroutable, string? detail, Exception innerException)
        : base(BuildMessage(messageTypeName, correlationId, isUnroutable, detail), innerException)
    {
        MessageTypeName = messageTypeName;
        CorrelationId = correlationId;
        IsUnroutable = isUnroutable;
    }

    public string MessageTypeName { get; }

    public Guid CorrelationId { get; }

    /// <summary><c>true</c> if the message could not be routed to any destination rather than being rejected by one.</summary>
    public bool IsUnroutable { get; }

    private static string BuildMessage(string messageTypeName, Guid correlationId, bool isUnroutable, string? detail)
    {
        var outcome = isUnroutable ? "returned as unroutable" : "nacked";
        var prefix = $"Publish of '{messageTypeName}' for correlation id '{correlationId}' was {outcome}";
        return detail is null ? $"{prefix} by the broker." : $"{prefix}: {detail}";
    }
}
