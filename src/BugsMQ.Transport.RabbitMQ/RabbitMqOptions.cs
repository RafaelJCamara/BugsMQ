namespace BugsMQ.Transport.RabbitMQ;

public sealed class RabbitMqOptions
{
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";

    public string ClientProvidedName { get; set; } = "BugsMQ";

    public string ExchangeName { get; set; } = "bugsmq.saga.events";

    public string DeadLetterExchangeName { get; set; } = "bugsmq.dlx";
}
