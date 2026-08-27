using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.RabbitMq;

namespace VSaga.Transport.RabbitMQ.Tests;

public sealed record PingMessage(string Text);

#pragma warning disable CA1001 // _connectionManager is disposed in DisposeAsync() via xUnit's IAsyncLifetime, not IAsyncDisposable
public sealed class RabbitMqTransportTests : IAsyncLifetime
{
#pragma warning restore CA1001
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management").Build();
    private RabbitMqConnectionManager _connectionManager = null!;
    private RabbitMqTransport _transport = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var options = new RabbitMqOptions
        {
            ConnectionString = _container.GetConnectionString(),
            ExchangeName = "vsaga.saga.events.test",
            DeadLetterExchangeName = "vsaga.dlx.test",
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

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "vsaga.test.ping-queue");
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

        var subscription = new TransportSubscription("TestConsumer2", [typeof(PingMessage)], "vsaga.test.direct-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        await _transport.SendAsync("vsaga.test.direct-queue", new PingMessage("direct"), MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(correlationId, (await tcs.Task).CorrelationId);
    }

    [Fact]
    public async Task SendRaw_DeliversDirectlyToNamedQueueWithoutExchange()
    {
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer3", [typeof(PingMessage)], "vsaga.test.direct-raw-queue");
        using var handle = await _transport.SubscribeAsync(subscription, async (received, ct) =>
        {
            tcs.TrySetResult(received);
            await received.Ack.AckAsync(ct);
        });

        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PingMessage("raw-direct"));
        await _transport.SendRawAsync("vsaga.test.direct-raw-queue", nameof(PingMessage), body, MessageEnvelope.New(correlationId));

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(tcs.Task, completed);

        var received = await tcs.Task;
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);
    }

    [Fact]
    public async Task Publish_ToUnboundRoutingKey_ThrowsUnroutablePublishException()
    {
        // No subscriber has ever bound a queue for this message type, so with publisher confirms +
        // mandatory:true the broker must return it as unroutable rather than silently dropping it.
        var ex = await Assert.ThrowsAsync<MessageTransportPublishException>(() =>
            _transport.PublishAsync(new PingMessage("nobody's listening"), MessageEnvelope.New(Guid.NewGuid())));

        Assert.True(ex.IsUnroutable);
    }
}
