using System.Net;
using VSaga.Abstractions.Persistence;
using VSaga.Dashboard.Api;

namespace VSaga.Http.Tests;

/// <summary>
/// Pins docs/http-based-sagas.md §5.3: a naive ctx.PublishAsync loopback stamps the *inbound* message's
/// causationId rather than the outbound call's own MessageId, so SagaMapBuilder's stitch misses, the
/// outbound entry falls through to ResolveUnstitchedDestinations and resolves to the saga's own type
/// (it *is* subscribed to the loopback message), rendering a bogus unanswered self-loop -- and the REST
/// host that was actually called never appears as a node at all. This proves the fix: a real node for
/// the call's host, and a stitched (non-unanswered) request/reply edge to it.
/// </summary>
public sealed class CallHttpSagaMapTests
{
    [Fact]
    public async Task CallHttp_ProducesAStitchedEdgeToTheRemoteHost_NotASelfLoop()
    {
        await using var harness = CallHttpTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"body":"charged"}"""));

        await harness.WhenAsync(new BeginHttpCall("REQ-MAP-1"));
        await harness.AssertStateAsync(harness.Saga.Succeeded);

        var state = await harness.FindStateAsync();
        Assert.NotNull(state);

        var summary = new SagaSummary(harness.CorrelationId, harness.Saga.SagaType, state.Kind, state.CurrentState,
            state.Status, state.CreatedAtUtc, state.UpdatedAtUtc, state.Version, state.ParentSagaType, state.ParentCorrelationId);
        var timeline = await harness.GetTimelineAsync();

        var map = SagaMapBuilder.Build(summary, timeline, topology: []);

        // Not asserting the absence of any "unresolved:*" node here: PublishAfterCommitAsync's own
        // auto-logged entry for the loopback publish (HttpCallSucceeded re-entering this same saga) is a
        // separate, additional timeline pair this fix deliberately doesn't touch -- with no topology
        // registry wired into this harness, that pair's own outbound leg falls through to
        // ResolveUnstitchedDestinations and renders its own "unresolved:HttpCallSucceeded" node. That is
        // a pre-existing characteristic of how PublishAfterCommitAsync is logged (§5.1), independent of
        // whether HttpCallDefinition's own request/reply pair -- the thing this test pins -- stitches
        // correctly.
        const string host = "call-target.test";
        var sagaType = harness.Saga.SagaType;
        Assert.Contains(map.Nodes, n => string.Equals(n.Id, host, StringComparison.Ordinal) && n.Kind != SagaMapNodeKind.Unresolved);

        var toHost = Assert.Single(map.Edges, e => string.Equals(e.FromNodeId, sagaType, StringComparison.Ordinal) && string.Equals(e.ToNodeId, host, StringComparison.Ordinal));
        Assert.False(toHost.Unanswered);

        // The reply leg back, exactly like every existing broker-based request/reply on the map.
        Assert.Contains(map.Edges, e => string.Equals(e.FromNodeId, host, StringComparison.Ordinal) && string.Equals(e.ToNodeId, sagaType, StringComparison.Ordinal));
    }
}
