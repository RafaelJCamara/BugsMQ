using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace VSaga.Chaos;

/// <summary>
/// Simulates an unroutable/vanished publish: sets <see cref="OutboundMessageContext.Suppressed"/> so
/// <c>MiddlewarePipelineTransport</c>'s terminal skips the real send, then still calls
/// <c>nextAsync</c> — mirrors <c>MessageMiddleware.cs</c>'s documented contract for this
/// flag exactly. From the caller's perspective the publish simply returns, same as if it had
/// succeeded; nothing ever arrives at the other end.
/// </summary>
public sealed class DropOutboundMiddleware(
    ChaosOptions options,
    IChaosRandomSource random,
    ILogger<DropOutboundMiddleware> logger) : IOutboundMessageMiddleware
{
    public Task InvokeAsync(OutboundMessageContext context, Func<OutboundMessageContext, Task> nextAsync)
    {
        if (random.RollTrigger(options.Drop.Probability))
        {
            logger.LogWarning("Chaos: dropping outbound {MessageType} to {Destination} (simulated unroutable/lost publish)",
                context.Message.GetType().Name, context.DestinationHint);
            context.Suppressed = true;
        }

        return nextAsync(context);
    }
}

/// <summary>
/// Simulates a message the broker delivered but that was lost before reaching the handler.
/// Suppressing here means the terminal handler — which normally owns the ack — never runs, so this
/// middleware acks the delivery itself before returning; otherwise the message would sit
/// unacknowledged forever and eventually exhaust the consumer's prefetch window. Deliberately does
/// not call <c>nextAsync</c> when triggered: unlike the outbound case there's no
/// downstream code left that needs a chance to run against an already-decided drop.
/// </summary>
public sealed class DropInboundMiddleware(
    ChaosOptions options,
    IChaosRandomSource random,
    ILogger<DropInboundMiddleware> logger) : IInboundMessageMiddleware
{
    public async Task InvokeAsync(InboundMessageContext context, Func<InboundMessageContext, Task> nextAsync)
    {
        if (random.RollTrigger(options.Drop.Probability))
        {
            logger.LogWarning("Chaos: dropping inbound {MessageType} (correlation {CorrelationId}) (simulated lost message)",
                context.Message.MessageTypeName, context.Message.CorrelationId);
            context.Suppressed = true;
            await context.Message.Ack.AckAsync();
            return;
        }

        await nextAsync(context);
    }
}
