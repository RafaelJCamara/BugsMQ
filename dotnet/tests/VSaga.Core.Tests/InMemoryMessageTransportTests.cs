using System.Text.Json;
using VSaga.Abstractions.Transport;
using VSaga.Transport.InMemory;

namespace VSaga.Core.Tests;

public sealed record PingMessage(string Text);

/// <summary>Structural mirror of the broker-backed adapters' own SendRawAsync tests (RabbitMqTransportTests, etc.), covering the one transport that needs no Docker container to exercise directly.</summary>
public sealed class InMemoryMessageTransportTests
{
    [Fact]
    public async Task SendRaw_DeliversDirectlyToNamedDestination()
    {
        var transport = new InMemoryMessageTransport();
        var correlationId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = new TransportSubscription("TestConsumer", [typeof(PingMessage)], "vsaga.test.direct-raw-queue");
        using var handle = await transport.SubscribeAsync(subscription, (received, _) =>
        {
            tcs.TrySetResult(received);
            return Task.CompletedTask;
        });

        var body = JsonSerializer.SerializeToUtf8Bytes(new PingMessage("raw-direct"));
        await transport.SendRawAsync("vsaga.test.direct-raw-queue", nameof(PingMessage), body, MessageEnvelope.New(correlationId));

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(correlationId, received.CorrelationId);
        Assert.Equal(nameof(PingMessage), received.MessageTypeName);

        var published = Assert.Single(transport.GetPublished());
        Assert.Equal("vsaga.test.direct-raw-queue", published.Destination);
        Assert.Null(published.Message); // raw path never carries a CLR object, unlike SendAsync<TMessage>
    }
}
