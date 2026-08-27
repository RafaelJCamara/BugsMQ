using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Chaos.Tests;

public sealed class DropMessageMiddlewareTests
{
    private static ChaosOptions NewOptions(double probability) =>
        new() { Drop = new DropFaultOptions { Probability = probability } };

    [Fact]
    public async Task Outbound_Triggered_SuppressesButStillCallsNext()
    {
        var middleware = new DropOutboundMiddleware(NewOptions(1.0), new FixedChaosRandomSource(0.0), NullLogger<DropOutboundMiddleware>.Instance);
        var context = TestFactory.NewOutboundContext();
        var nextCalled = false;

        await middleware.InvokeAsync(context, ctx =>
        {
            nextCalled = true;
            Assert.True(ctx.Suppressed);
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.True(context.Suppressed);
    }

    [Fact]
    public async Task Outbound_NotTriggered_DoesNotSuppress()
    {
        var middleware = new DropOutboundMiddleware(NewOptions(0.0), new FixedChaosRandomSource(0.0), NullLogger<DropOutboundMiddleware>.Instance);
        var context = TestFactory.NewOutboundContext();
        var nextCalled = false;

        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.False(context.Suppressed);
    }

    [Fact]
    public async Task Inbound_Triggered_AcksItselfAndSuppressesWithoutCallingNext()
    {
        var middleware = new DropInboundMiddleware(NewOptions(1.0), new FixedChaosRandomSource(0.0), NullLogger<DropInboundMiddleware>.Instance);
        var ack = new RecordingAckContext();
        var context = new InboundMessageContext(TestFactory.NewReceivedMessage(ack));
        var nextCalled = false;

        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.True(context.Suppressed);
        Assert.Equal(1, ack.AckCount);
        Assert.Equal(0, ack.NackCount);
    }

    [Fact]
    public async Task Inbound_NotTriggered_CallsNextWithoutAckingOrSuppressing()
    {
        var middleware = new DropInboundMiddleware(NewOptions(0.0), new FixedChaosRandomSource(0.0), NullLogger<DropInboundMiddleware>.Instance);
        var ack = new RecordingAckContext();
        var context = new InboundMessageContext(TestFactory.NewReceivedMessage(ack));
        var nextCalled = false;

        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.False(context.Suppressed);
        Assert.Equal(0, ack.AckCount);
    }
}
