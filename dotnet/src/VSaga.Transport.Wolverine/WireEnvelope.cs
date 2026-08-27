namespace VSaga.Transport.Wolverine;

/// <summary>
/// The actual wire format this transport puts on RabbitMQ via Wolverine's raw-send API
/// (<c>IDestinationEndpoint.SendRawMessageAsync</c>) — deliberately self-describing and independent of
/// Wolverine's own Envelope-to-AMQP-properties mapping, so that VSaga's four well-known headers (and
/// everything else in <see cref="VSaga.Abstractions.Transport.MessageEnvelope.Headers"/>) round-trip
/// byte for byte regardless of whatever Wolverine itself does with its own <c>Envelope.Headers</c>.
/// Wolverine's <c>Envelope.Data</c> carries these exact bytes untouched from publish to receive — see
/// <see cref="RawDispatchRegistry.DispatchAsync"/>, which is the only place this type is deserialized.
/// </summary>
internal sealed record WireEnvelope(
    string MessageTypeName,
    Guid CorrelationId,
    string MessageId,
    Dictionary<string, string> Headers,
    byte[] Body);
