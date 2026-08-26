namespace BugsMQ.Transport.MassTransit;

/// <summary>
/// The single MassTransit message contract every BugsMQ message travels as. BugsMQ message types are
/// only known at runtime, as <see cref="Type"/> instances on
/// <see cref="Abstractions.Transport.TransportSubscription.MessageTypes"/> — never as compile-time
/// generic parameters, which is what MassTransit's own per-type exchange topology (<c>Publish&lt;T&gt;</c>,
/// <c>IConsumer&lt;T&gt;</c>) is built around. Rather than fight that, every BugsMQ publish/send carries
/// its already-JSON-serialized body plus the source type's name as this one fixed contract, exactly the
/// way <c>RabbitMqTransport</c> treats the RabbitMQ.Client message body as opaque bytes one layer down —
/// <see cref="MessageTypeName"/> becomes both the payload's real type marker and the routing key
/// MassTransit's own <c>UseRoutingKeyFormatter</c> reads (see <c>ServiceCollectionExtensions</c>). The
/// four BugsMQ envelope headers still ride on MassTransit's own <c>SendContext.Headers</c>/
/// <c>ConsumeContext.Headers</c>, not on this record, so they exercise MassTransit's real header
/// pipeline rather than being smuggled through as opaque payload data.
/// </summary>
public sealed record BugsMqEnvelopeMessage(string MessageTypeName, byte[] Body);
