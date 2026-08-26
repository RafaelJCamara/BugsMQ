namespace BugsMQ.Transport.MassTransit;

public sealed class MassTransitOptions
{
    /// <summary>Passed straight to MassTransit's RabbitMQ <c>cfg.Host(new Uri(...))</c>, same convention as <see cref="Transport.RabbitMQ.RabbitMqOptions.ConnectionString"/>.</summary>
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";

    /// <summary>
    /// The single durable topic exchange every BugsMQ message published through this adapter travels
    /// over, mirroring <c>RabbitMqOptions.ExchangeName</c>. Every message shares one MassTransit message
    /// contract (<see cref="BugsMqEnvelopeMessage"/>) forced onto this exchange name via
    /// <c>cfg.Message&lt;BugsMqEnvelopeMessage&gt;(m =&gt; m.SetEntityName(...))</c> — BugsMQ's own
    /// runtime-dynamic message types (only known as <see cref="Type"/> instances on
    /// <c>TransportSubscription.MessageTypes</c>, never as compile-time generic parameters MassTransit's
    /// own per-type topology needs) are carried as routing keys, not as distinct MassTransit contracts.
    /// </summary>
    public string ExchangeName { get; set; } = "bugsmq.saga.events";
}
