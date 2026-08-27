using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace VSaga.Chaos;

/// <summary>
/// Re-publishes the same outbound message (same <see cref="MessageEnvelope.MessageId"/>) one or more
/// extra times after the real publish, simulating a broker's at-least-once delivery guarantee at the
/// point of sending. Each extra call to <c>nextAsync</c> becomes its own independent
/// broker delivery with its own delivery tag on the receiving end, so — unlike duplicating inbound —
/// there's no shared ack to worry about double-acknowledging.
/// </summary>
public sealed class DuplicateOutboundMiddleware(
    ChaosOptions options,
    IChaosRandomSource random,
    ILogger<DuplicateOutboundMiddleware> logger) : IOutboundMessageMiddleware
{
    public async Task InvokeAsync(OutboundMessageContext context, Func<OutboundMessageContext, Task> nextAsync)
    {
        await nextAsync(context);

        if (!random.RollTrigger(options.Duplicate.Probability))
            return;

        logger.LogWarning("Chaos: re-publishing outbound {MessageType} to {Destination} {Count} extra time(s) (simulated at-least-once redelivery)",
            context.Message.GetType().Name, context.DestinationHint, options.Duplicate.ExtraDeliveries);

        for (var i = 0; i < options.Duplicate.ExtraDeliveries; i++)
            await nextAsync(context);
    }
}

/// <summary>
/// Redelivers the same inbound message to the handler one or more extra times after the real
/// delivery, simulating a broker's at-least-once guarantee on the receiving end — this is what
/// exercises the saga engine's own duplicate-message dedup (<c>ISagaEventLogStore.IsDuplicateAsync</c>).
/// The genuine delivery runs through <c>nextAsync</c> unmodified (real ack included); each
/// synthetic extra "redelivery" wraps a copy of the message with a no-op ack instead of reusing the
/// original <see cref="IMessageAckContext"/>, since the one physical broker delivery behind it must
/// only ever be acked/nacked once.
/// </summary>
public sealed class DuplicateInboundMiddleware(
    ChaosOptions options,
    IChaosRandomSource random,
    ILogger<DuplicateInboundMiddleware> logger) : IInboundMessageMiddleware
{
    public async Task InvokeAsync(InboundMessageContext context, Func<InboundMessageContext, Task> nextAsync)
    {
        await nextAsync(context);

        if (!random.RollTrigger(options.Duplicate.Probability))
            return;

        logger.LogWarning("Chaos: redelivering inbound {MessageType} (correlation {CorrelationId}) {Count} extra time(s) (simulated at-least-once redelivery)",
            context.Message.MessageTypeName, context.Message.CorrelationId, options.Duplicate.ExtraDeliveries);

        for (var i = 0; i < options.Duplicate.ExtraDeliveries; i++)
        {
            var duplicateMessage = context.Message with { Ack = NoOpMessageAckContext.Instance };
            await nextAsync(new InboundMessageContext(duplicateMessage));
        }
    }
}

/// <summary>Swallows ack/nack for a synthetic duplicate delivery — the real, single physical delivery it's a copy of owns the actual ack decision.</summary>
internal sealed class NoOpMessageAckContext : IMessageAckContext
{
    public static readonly NoOpMessageAckContext Instance = new();

    private NoOpMessageAckContext()
    {
    }

    public Task AckAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NackAsync(bool requeue, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
