using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace VSaga.Chaos;

/// <summary>
/// Delays an outbound publish before it reaches the rest of the pipeline (and eventually the real
/// transport). <see cref="TimeProvider"/> is injected (not <see cref="Task.Delay(TimeSpan)"/> directly)
/// so tests can drive the wait with <c>FakeTimeProvider</c> instead of actually waiting.
/// </summary>
public sealed class DelayOutboundMiddleware(
    ChaosOptions options,
    IChaosRandomSource random,
    TimeProvider timeProvider,
    ILogger<DelayOutboundMiddleware> logger) : IOutboundMessageMiddleware
{
    public async Task InvokeAsync(OutboundMessageContext context, Func<OutboundMessageContext, Task> nextAsync)
    {
        if (random.RollTrigger(options.Delay.Probability))
        {
            var delay = random.NextDelay(options.Delay.MinDelay, options.Delay.MaxDelay);
            logger.LogWarning("Chaos: delaying outbound {MessageType} to {Destination} by {Delay}",
                context.Message.GetType().Name, context.DestinationHint, delay);
            await Task.Delay(delay, timeProvider);
        }

        await nextAsync(context);
    }
}

/// <summary>Delays an inbound delivery before it reaches the rest of the pipeline (and eventually the saga/participant handler). Same rationale as <see cref="DelayOutboundMiddleware"/>.</summary>
public sealed class DelayInboundMiddleware(
    ChaosOptions options,
    IChaosRandomSource random,
    TimeProvider timeProvider,
    ILogger<DelayInboundMiddleware> logger) : IInboundMessageMiddleware
{
    public async Task InvokeAsync(InboundMessageContext context, Func<InboundMessageContext, Task> nextAsync)
    {
        if (random.RollTrigger(options.Delay.Probability))
        {
            var delay = random.NextDelay(options.Delay.MinDelay, options.Delay.MaxDelay);
            logger.LogWarning("Chaos: delaying inbound {MessageType} (correlation {CorrelationId}) by {Delay}",
                context.Message.MessageTypeName, context.Message.CorrelationId, delay);
            await Task.Delay(delay, timeProvider);
        }

        await nextAsync(context);
    }
}
