using VSaga.Abstractions.Transport;

namespace VSaga.Chaos.Tests;

internal sealed record TestMessage(string Text);

/// <summary>Fixed-output stand-in for <see cref="IChaosRandomSource"/> so probability rolls and delay-range picks are deterministic in tests.</summary>
internal sealed class FixedChaosRandomSource(double value) : IChaosRandomSource
{
    public double NextDouble() => value;
}

/// <summary>Records how many times it was acked/nacked, so tests can assert a physical delivery was settled exactly once.</summary>
internal sealed class RecordingAckContext : IMessageAckContext
{
    public int AckCount { get; private set; }

    public int NackCount { get; private set; }

    public Task AckAsync(CancellationToken cancellationToken = default)
    {
        AckCount++;
        return Task.CompletedTask;
    }

    public Task NackAsync(bool requeue, CancellationToken cancellationToken = default)
    {
        NackCount++;
        return Task.CompletedTask;
    }
}

internal static class TestFactory
{
    public static OutboundMessageContext NewOutboundContext(string destinationHint = "publish") =>
        new(new TestMessage("hello"), MessageEnvelope.New(Guid.NewGuid()), destinationHint);

    public static ReceivedMessage NewReceivedMessage(IMessageAckContext ack) =>
        new(nameof(TestMessage), Guid.NewGuid(), Guid.NewGuid().ToString("N"), new byte[] { 1, 2, 3 },
            new Dictionary<string, string>(StringComparer.Ordinal), ack);
}
