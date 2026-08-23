using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.RabbitMq;

namespace BugsMQ.Transport.RabbitMQ.Tests;

public sealed record PingMessage(string Text);

public sealed class RabbitMqTransportTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private RabbitMqConnectionManager _connectionManager = null!;
    private RabbitMqTransport _transport = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new RabbitMqOptions
        {
            ConnectionString = _container.GetConnectionString(),
            ExchangeName = "bugsmq.saga.events.test",
            DeadLetterExchangeName = "bugsmq.dlx.test",
        };

        _connectionManager = new RabbitMqConnectionManager(options);
        _transport = new RabbitMqTransport(_connectionManager, options, new DefaultRoutingKeyConvention(), NullLogger<RabbitMqTransport>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _connectionManager.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task PublishAndSubscribe_DeliversMessageWithCorrelationAndType()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "bugsmq.test.ping-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        await _transport.PublishAsync(new PingMessage("hello"), MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);

        var payload = System.Text.Json.JsonSerializer.Deserialize<PingMessage>(received.Body.Span);
        Assert.Equal("hello", payload!.Text);
    }

    [Fact]
    public async Task Send_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer2", [typeof(PingMessage)], "bugsmq.test.direct-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        await _transport.SendAsync("bugsmq.test.direct-queue", new PingMessage("direct"), MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(correlationId, (await tcs.Task).CorrelationId);
    }
}
