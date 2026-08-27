namespace VSaga.Transport.RabbitMQ;

public sealed class RabbitMqOptions
{
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";

    public string ClientProvidedName { get; set; } = "VSaga";

    public string ExchangeName { get; set; } = "vsaga.saga.events";

    public string DeadLetterExchangeName { get; set; } = "vsaga.dlx";
}
