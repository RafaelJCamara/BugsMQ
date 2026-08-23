namespace BugsMQ.Abstractions.Transport;

/// <summary>Metadata stamped onto every outbound message alongside its payload.</summary>
public sealed record MessageEnvelope(
    Guid CorrelationId,
    string MessageId,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public static MessageEnvelope New(Guid correlationId, IReadOnlyDictionary<string, string>? headers = null) =>
        new(correlationId, Guid.NewGuid().ToString("N"), headers);
}
