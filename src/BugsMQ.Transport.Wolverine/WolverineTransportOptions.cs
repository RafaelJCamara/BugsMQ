namespace BugsMQ.Transport.Wolverine;

/// <summary>Mirrors <c>BugsMQ.Transport.RabbitMQ.RabbitMqOptions</c>'s shape for the Wolverine adapter.</summary>
public sealed class WolverineTransportOptions
{
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";

    public string ExchangeName { get; set; } = "bugsmq.saga.events";
}
