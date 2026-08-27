namespace VSaga.Chaos;

/// <summary>
/// Root config for VSaga.Chaos, bound the same way as RabbitMqOptions/SagaOrchestratorOptions — see
/// <see cref="ServiceCollectionExtensions.AddVSagaChaos"/>. Each fault type is independently tuned
/// and gated by its own <c>Enabled</c> flag; a disabled fault is never registered into the
/// outbound/inbound middleware pipeline at all, so it costs nothing at runtime (not even a
/// probability roll) rather than being a no-op check on every message.
/// </summary>
public sealed class ChaosOptions
{
    public DelayFaultOptions Delay { get; set; } = new();

    public DropFaultOptions Drop { get; set; } = new();

    public DuplicateFaultOptions Duplicate { get; set; } = new();
}

/// <summary>Injects random extra latency before a publish/delivery proceeds through the rest of the pipeline.</summary>
public sealed class DelayFaultOptions
{
    public bool Enabled { get; set; }

    public bool ApplyToOutbound { get; set; } = true;

    public bool ApplyToInbound { get; set; } = true;

    /// <summary>Chance, per message per direction, that this fault triggers on a given send/delivery.</summary>
    public double Probability { get; set; } = 0.1;

    public TimeSpan MinDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Silently vanishes a message. Outbound suppresses the actual send, simulating a publish that never
/// reaches a queue (an unroutable message, or one lost between the app and the broker). Inbound acks
/// the delivery itself and suppresses it before the handler runs, simulating a message the broker
/// delivered but that was lost before being processed — see <see cref="DropInboundMiddleware"/> for
/// why it must own the ack in that case.
/// </summary>
public sealed class DropFaultOptions
{
    public bool Enabled { get; set; }

    public bool ApplyToOutbound { get; set; } = true;

    public bool ApplyToInbound { get; set; } = true;

    public double Probability { get; set; } = 0.05;
}

/// <summary>
/// Re-delivers/re-publishes a message one or more extra times, simulating a broker's at-least-once
/// delivery guarantee (the same physical message arriving — or being sent — more than once).
/// </summary>
public sealed class DuplicateFaultOptions
{
    public bool Enabled { get; set; }

    public bool ApplyToOutbound { get; set; } = true;

    public bool ApplyToInbound { get; set; } = true;

    public double Probability { get; set; } = 0.05;

    /// <summary>How many extra deliveries/publishes to add on top of the original when this fault triggers.</summary>
    public int ExtraDeliveries { get; set; } = 1;
}
