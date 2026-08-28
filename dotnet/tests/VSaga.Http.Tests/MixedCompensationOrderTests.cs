using System.Net;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;

namespace VSaga.Http.Tests;

/// <summary>
/// docs/design/mixed-sagas.md §4/§9: a compensation delegate performing a broker publish (<c>ctx.PublishAsync</c>)
/// and a <c>ctx.CallHttpAsync</c> in order, asserting both happened -- the two-hop compensation shape
/// MixedFulfilmentSaga's own §7 compensation needs, at the smallest scale that proves it out ahead of the
/// sample existing.
/// </summary>
public sealed class MixedCompensationOrderTests
{
    [Fact]
    public async Task Compensation_PublishesToBrokerThenCallsHttp_AndTheLoopbackDrivesTheSagaToFailed()
    {
        await using var harness = CallHttpAsyncTestHarness.CreateForCompensation(_ =>
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"body":"voided"}"""));

        await harness.WhenAsync(new BeginMixedFlow("REQ-MIX-1"));
        await harness.WhenAsync(new MixedFlowFailed());

        var state = await harness.AssertStateAsync(harness.Saga.Reversed);
        Assert.Equal(SagaStatus.Failed, state.Status);

        harness.AssertPublished<ReleaseHold>(m => string.Equals(m.RequestId, "REQ-MIX-1", StringComparison.Ordinal));

        var timeline = await harness.GetTimelineAsync();
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStarted);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.CompensationStepSucceeded);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.MessagePublished && string.Equals(e.MessageType, nameof(ReleaseHold), StringComparison.Ordinal));
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.MessagePublished && (e.MessageType ?? string.Empty).StartsWith("POST ", StringComparison.Ordinal));
    }
}
