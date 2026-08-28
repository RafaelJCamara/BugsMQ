using System.Text.Json;
using VSaga.Abstractions.Transport;

namespace VSaga.Transport.Http.Tests;

// Empty record is intentional: the gate is keyed purely on MessageEnvelope.CorrelationId, so this
// probe's own payload carries nothing -- both racing "instances" of it differ only by which
// correlation id their envelope carries.
#pragma warning disable S2094
public sealed record GateProbeMessage;
#pragma warning restore S2094

/// <summary>
/// production-readiness.md §5.4 (item 15): pins the hazard directly at the layer it lives in, rather
/// than only inferring it from <c>HttpInboundDispatcher</c>'s source. §5.3's business-key fallback lets
/// two messages carrying two <em>different</em> transport correlation ids resolve to the very same saga
/// instance -- but <see cref="HttpInboundDispatcher"/>'s per-correlation dispatch gate
/// (<c>DispatchToSubscribersAsync</c>, keyed on <c>received.CorrelationId</c>) has no visibility into
/// that resolution; it only ever sees the transport id. This test proves the gate really does let two
/// such messages run fully concurrently -- the mirror image of
/// <see cref="HttpTransportTests.SyncReply_IsNotDispatchedInlineDuringThePublishingStep"/>, which proves
/// the gate DOES serialize two dispatches sharing the SAME correlation id. Together they show the gate's
/// protection is real but keyed on the wrong thing once business-key correlation is in play -- the
/// actual backstop for that case is the snapshot store's optimistic-concurrency check, pinned
/// separately (and empirically) by SagaOrchestratorConcurrencyRedeliveryTests in VSaga.Core.Tests.
/// </summary>
public sealed class HttpInboundDispatcherGateHazardTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Gate_DoesNotSerializeTwoDifferentCorrelationIdsEvenWhenTheyShareALogicalSagaInstance()
    {
        var registry = new NodeRegistry();
        await using var node = await HttpTestNode.StartAsync("solo.test", registry, _ => { });
        var transport = node.GetRequiredService<HttpMessageTransport>();

        var heldCorrelationId = Guid.NewGuid();
        var otherCorrelationId = Guid.NewGuid();

        var heldHandlerStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeldHandlerTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherHandlerRanTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Both messages route to the exact same local subscriber and the exact same dispatch code path
        // (DispatchToSubscribersAsync, via the pump -- no route/endpoint is configured, purely local
        // dispatch exactly like Publish_OfLocallySubscribedType_ReEntersLocalSubscriber in
        // HttpTransportTests). The only thing that ever differs between them is the correlation id.
        await transport.SubscribeAsync(new TransportSubscription("GateProbe", [typeof(GateProbeMessage)], "solo-gate-probe-queue"),
            async (received, _) =>
            {
                if (received.CorrelationId == heldCorrelationId)
                {
                    heldHandlerStartedTcs.TrySetResult();
                    await releaseHeldHandlerTcs.Task; // simulates a saga step still in flight, gate held
                }
                else
                {
                    otherHandlerRanTcs.TrySetResult();
                }
            });

        await PublishGateProbeAsync(transport, heldCorrelationId);
        await heldHandlerStartedTcs.Task.WaitAsync(Timeout); // the held dispatch now owns its own gate entry

        // If HttpInboundDispatcher's gate were keyed on the *resolved saga instance* instead of the raw
        // transport correlation id -- which it is not, per §5.4 -- this second dispatch would block
        // until releaseHeldHandlerTcs completes below, exactly as the SAME-correlation-id case does in
        // HttpTransportTests.SyncReply_IsNotDispatchedInlineDuringThePublishingStep. Because the gate is
        // keyed purely on received.CorrelationId, and this is a different (if unarmed) Guid, it gets its
        // own independent SemaphoreSlim and is free to run immediately.
        await PublishGateProbeAsync(transport, otherCorrelationId);

        await otherHandlerRanTcs.Task.WaitAsync(Timeout);

        // Proves it wasn't scheduling luck: the first dispatch's gate is still genuinely held at the
        // moment the second one already completed.
        Assert.False(releaseHeldHandlerTcs.Task.IsCompleted);
        releaseHeldHandlerTcs.SetResult();
    }

    private static Task PublishGateProbeAsync(HttpMessageTransport transport, Guid correlationId) =>
        transport.PublishRawAsync(nameof(GateProbeMessage), JsonSerializer.SerializeToUtf8Bytes(new GateProbeMessage()),
            MessageEnvelope.New(correlationId));
}
