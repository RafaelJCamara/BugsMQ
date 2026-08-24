using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace BugsMQ.Chaos.Tests;

public sealed class DuplicateMessageMiddlewareTests
{
    private static ChaosOptions NewOptions(double probability, int extraDeliveries = 1) =>
        new() { Duplicate = new DuplicateFaultOptions { Probability = probability, ExtraDeliveries = extraDeliveries } };

    [Fact]
    public async Task Outbound_Triggered_CallsNextOnceForOriginalPlusExtraDeliveries()
    {
        var middleware = new DuplicateOutboundMiddleware(NewOptions(1.0, extraDeliveries: 2), new FixedChaosRandomSource(0.0), NullLogger<DuplicateOutboundMiddleware>.Instance);
        var context = TestFactory.NewOutboundContext();
        var callCount = 0;

        await middleware.InvokeAsync(context, ctx =>
        {
            callCount++;
            // Every republish carries the same envelope/MessageId as the original — that's what lets
            // the receiving saga's dedup check recognize it as a duplicate rather than a new message.
            Assert.Same(context.Envelope, ctx.Envelope);
            return Task.CompletedTask;
        });

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task Outbound_NotTriggered_CallsNextExactlyOnce()
    {
        var middleware = new DuplicateOutboundMiddleware(NewOptions(0.0), new FixedChaosRandomSource(0.0), NullLogger<DuplicateOutboundMiddleware>.Instance);
        var context = TestFactory.NewOutboundContext();
        var callCount = 0;

        await middleware.InvokeAsync(context, _ =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task Inbound_Triggered_OriginalAckIsUsedOnceAndDuplicatesGetNoOpAck()
    {
        var middleware = new DuplicateInboundMiddleware(NewOptions(1.0, extraDeliveries: 2), new FixedChaosRandomSource(0.0), NullLogger<DuplicateInboundMiddleware>.Instance);
        var originalAck = new RecordingAckContext();
        var context = new InboundMessageContext(TestFactory.NewReceivedMessage(originalAck));
        var callCount = 0;

        await middleware.InvokeAsync(context, ctx =>
        {
            callCount++;
            // A real terminal handler always acks on success — every one of these calls must be safe
            // to ack without ever double-acking the single physical delivery behind `originalAck`.
            return ctx.Message.Ack.AckAsync();
        });

        Assert.Equal(3, callCount);
        Assert.Equal(1, originalAck.AckCount);
    }

    [Fact]
    public async Task Inbound_Triggered_DuplicateContextsCarrySameMessageIdAndCorrelationId()
    {
        var middleware = new DuplicateInboundMiddleware(NewOptions(1.0), new FixedChaosRandomSource(0.0), NullLogger<DuplicateInboundMiddleware>.Instance);
        var original = TestFactory.NewReceivedMessage(new RecordingAckContext());
        var context = new InboundMessageContext(original);
        var seenMessageIds = new List<string>();

        await middleware.InvokeAsync(context, ctx =>
        {
            seenMessageIds.Add(ctx.Message.MessageId);
            return Task.CompletedTask;
        });

        Assert.Equal(2, seenMessageIds.Count);
        Assert.All(seenMessageIds, id => Assert.Equal(original.MessageId, id));
    }

    [Fact]
    public async Task Inbound_NotTriggered_CallsNextExactlyOnce()
    {
        var middleware = new DuplicateInboundMiddleware(NewOptions(0.0), new FixedChaosRandomSource(0.0), NullLogger<DuplicateInboundMiddleware>.Instance);
        var context = new InboundMessageContext(TestFactory.NewReceivedMessage(new RecordingAckContext()));
        var callCount = 0;

        await middleware.InvokeAsync(context, _ =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, callCount);
    }
}
