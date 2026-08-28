using System.Diagnostics;
using System.Globalization;
using VSaga.Abstractions.Transport;

namespace VSaga.Core.Tests;

/// <summary>
/// <see cref="MessageEnvelope.From"/>'s wiring of the current <see cref="Activity"/>'s W3C trace
/// context onto outbound headers (production readiness §8.16). No producer span exists yet
/// (that's item 18), so these exercise the injection directly against a hand-started
/// <see cref="Activity"/> standing in for one, the same way <see cref="MessageEnvelope.From"/> reads
/// whatever <see cref="Activity.Current"/> happens to be.
/// </summary>
public sealed class MessageEnvelopeTraceContextTests
{
    [Fact]
    public void From_WithAnActiveActivity_InjectsTraceParentMatchingIt()
    {
        using var activity = new Activity("test.operation").SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        try
        {
            var envelope = MessageEnvelope.From("OrderSaga", Guid.NewGuid());

            Assert.NotNull(envelope.Headers);
            Assert.True(envelope.Headers.TryGetValue("traceparent", out var traceparent));
            var expectedFlags = ((byte)activity.ActivityTraceFlags).ToString("x2", CultureInfo.InvariantCulture);
            Assert.Equal($"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-{expectedFlags}", traceparent);
        }
        finally
        {
            activity.Stop();
        }
    }

    [Fact]
    public void From_WithNoActiveActivity_OmitsTraceParent()
    {
        Assert.Null(Activity.Current); // guard the test's own assumption

        var envelope = MessageEnvelope.From("OrderSaga", Guid.NewGuid());

        Assert.NotNull(envelope.Headers);
        Assert.False(envelope.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public void From_StillMergesSourceServiceAndCausationIdAlongsideTraceParent()
    {
        using var activity = new Activity("test.operation").SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        try
        {
            var envelope = MessageEnvelope.From("OrderSaga", Guid.NewGuid(), causationId: "m1");

            Assert.Equal("OrderSaga", envelope.Headers![MessageEnvelope.SourceServiceHeader]);
            Assert.Equal("m1", envelope.Headers[MessageEnvelope.CausationIdHeader]);
            Assert.True(envelope.Headers.ContainsKey("traceparent"));
        }
        finally
        {
            activity.Stop();
        }
    }
}
