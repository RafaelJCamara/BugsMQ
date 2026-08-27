namespace VSaga.Transport.Brighter;

/// <summary>Mirrors <c>VSaga.Transport.RabbitMQ.RabbitMqOptions</c>'s shape so the two adapters are
/// config-swappable with the least surprise (see docker-compose.brighter.yml, which relies on this
/// binding to the same "RabbitMq" configuration section RabbitMqOptions already uses).</summary>
public sealed class BrighterOptions
{
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";

    public string ClientProvidedName { get; set; } = "VSaga";

    public string ExchangeName { get; set; } = "vsaga.saga.events";
}
