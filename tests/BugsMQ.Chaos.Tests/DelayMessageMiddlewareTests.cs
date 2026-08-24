using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BugsMQ.Chaos.Tests;

public sealed class DelayMessageMiddlewareTests
{
    private static ChaosOptions NewOptions(double probability) =>
        new() { Delay = new DelayFaultOptions { Probability = probability, MinDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(1) } };

    [Fact]
    public async Task Outbound_Triggered_DelaysUntilTimeAdvancesBeforeCallingNext()
    {
        var timeProvider = new FakeTimeProvider();
        var middleware = new DelayOutboundMiddleware(NewOptions(probability: 1.0), new FixedChaosRandomSource(0.0), timeProvider, NullLogger<DelayOutboundMiddleware>.Instance);
        var context = TestFactory.NewOutboundContext();
        var nextCalled = false;

        var invokeTask = middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.False(invokeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await invokeTask;

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Outbound_NotTriggered_CallsNextImmediatelyWithoutAdvancingTime()
    {
        var timeProvider = new FakeTimeProvider();
        var middleware = new DelayOutboundMiddleware(NewOptions(probability: 0.0), new FixedChaosRandomSource(0.0), timeProvider, NullLogger<DelayOutboundMiddleware>.Instance);
        var context = TestFactory.NewOutboundContext();
        var nextCalled = false;

        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Inbound_Triggered_DelaysUntilTimeAdvancesBeforeCallingNext()
    {
        var timeProvider = new FakeTimeProvider();
        var middleware = new DelayInboundMiddleware(NewOptions(probability: 1.0), new FixedChaosRandomSource(0.0), timeProvider, NullLogger<DelayInboundMiddleware>.Instance);
        var context = new InboundMessageContext(TestFactory.NewReceivedMessage(new RecordingAckContext()));
        var nextCalled = false;

        var invokeTask = middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.False(nextCalled);
        Assert.False(invokeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await invokeTask;

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Inbound_NotTriggered_CallsNextImmediatelyWithoutAdvancingTime()
    {
        var timeProvider = new FakeTimeProvider();
        var middleware = new DelayInboundMiddleware(NewOptions(probability: 0.0), new FixedChaosRandomSource(0.0), timeProvider, NullLogger<DelayInboundMiddleware>.Instance);
        var context = new InboundMessageContext(TestFactory.NewReceivedMessage(new RecordingAckContext()));
        var nextCalled = false;

        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
    }
}
