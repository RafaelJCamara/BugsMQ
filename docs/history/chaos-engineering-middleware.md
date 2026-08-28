# History: chaos-engineering transport middleware

> Preserved verbatim from the original `README.md`. Describes commit `13f7160` ("Add BugsMQ.Chaos
> fault-injection middleware and wire it into OrderProcessing"). See
> [`../chaos.md`](../chaos.md) for the current reference documentation.

---

## Chaos-engineering transport middleware

A new project, `VSaga.Chaos`, plugs three fault types into the `IOutboundMessageMiddleware`/
`IInboundMessageMiddleware` seam that `MiddlewarePipelineTransport` already wraps every transport in
— the seam the original v1 commit left in place unused specifically for this. `AddVSagaChaos(...)`
follows the same opt-in, never-registered-by-default convention as the existing
`LoggingOutboundMiddleware`/`LoggingInboundMiddleware` proof-of-concept: each of the three fault
types (`Delay`, `Drop`, `Duplicate`) is independently gated by its own `Enabled` flag (plus
`ApplyToOutbound`/`ApplyToInbound`), and a disabled fault is never even registered into the
pipeline — no runtime check, no cost — rather than registered-but-inert.

- **Delay** — waits a random `[MinDelay, MaxDelay]` before the publish/delivery continues through the
  rest of the pipeline. Uses an injected `TimeProvider` rather than `Task.Delay` directly, so tests
  drive it with `FakeTimeProvider` instead of actually waiting.
- **Drop** — outbound sets `OutboundMessageContext.Suppressed`, so the terminal skips the real send
  (the publish call returns normally; nothing ever arrives — simulating an unroutable or otherwise
  lost publish). Inbound sets `InboundMessageContext.Suppressed` **and acks the delivery itself**
  before returning without calling `nextAsync`: suppressing skips the terminal handler, which is
  normally what owns the ack, so without this the message would sit unacknowledged forever and
  eventually exhaust the consumer's prefetch window (`BasicQosAsync(prefetchCount: 32, ...)` in
  `RabbitMqTransport`) instead of behaving like a message silently lost after delivery.
- **Duplicate** — re-invokes `nextAsync` `ExtraDeliveries` extra times after the real one, simulating
  a broker's at-least-once guarantee. Outbound re-publishes are trivially safe (same `MessageId`,
  each becomes its own independent broker delivery on the receiving end). Inbound is the more
  interesting case: a genuine second delivery of the *same* physical message must never be acked
  twice, so the extra invocations wrap a copy of the message with a no-op `IMessageAckContext`
  (`NoOpMessageAckContext`) instead of reusing the real one — the real delivery's ack/nack decision is
  made exactly once, by the real invocation, regardless of how many synthetic duplicates run.

**What this does and doesn't exercise, by design.** All three faults operate purely at the transport
middleware layer, which sits *outside* `SagaOrchestrator.HandleAsync`'s own try/catch (confirmed by
reading `SagaRuntime.HandleReceivedAsync`, which passes `orchestrator.HandleAsync` itself as the
terminal handler `MiddlewarePipelineTransport.SubscribeAsync` wraps). That means chaos faults can't
reach — and deliberately don't try to fake — `HandleInfrastructureFailureAsync`'s bounded-redelivery/
`DeliveryExhausted` DLQ path, which is reserved for genuine infrastructure failures inside the
orchestrator itself (a deserialize error, a persistence-store exception); `HandleInfrastructureFailureAsync`'s
own redelivery publish also deliberately bypasses the middleware pipeline entirely
(`MiddlewarePipelineTransport.PublishRawAsync` forwards straight to the inner transport), so chaos
can't intercept it even indirectly. What chaos *does* exercise, end to end against the real
docker-compose stack: RabbitMQ publisher confirms continuing to work correctly under injected
latency and re-publishes; the `OrderSaga.AwaitingPayment` 30-second timeout (the one state in the
sample with `WithTimeout` configured) firing and compensating when a drop/delay makes a reply
never-arrive-in-time, including the race where a *delayed* reply finally lands after the timeout
already fired; and `ISagaEventLogStore.IsDuplicateAsync` silently absorbing a chaos-duplicated
message so it doesn't get processed (or its saga step re-run) twice. States without a configured
timeout have no such safety net — a dropped `ReserveInventory`/`ShipOrder` can leave that saga stuck
Running — which chaos testing usefully surfaces as a real, pre-existing gap in the sample's timeout
coverage rather than something `VSaga.Chaos` should paper over. **Closed in a later pass** — see
[`timeout-coverage-every-awaiting-state.md`](timeout-coverage-every-awaiting-state.md); all three awaiting states
now carry a timeout.

**Wiring.** `dotnet/samples/VSaga.Samples.OrderProcessing` calls `AddVSagaChaos` only when
`Chaos:Enabled` is `true` (`appsettings.json` defaults it to `false`, so plain `docker compose up`
is unaffected). `docker-compose.chaos.yml` is an overlay that turns all three faults on with sample
tuned probabilities:

```bash
docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build
```

**Live verification against the real stack caught a real bug in the tuning, and a real bug in
`VSaga.Core`.** First pass used `Delay.MaxDelay = 35s`, which is longer than the 8-second order
cadence: `RabbitMqTransport` gives each `SubscribeAsync` call one channel with a single sequential
consumer (`BasicQosAsync(prefetchCount: 32)`, one `ReceivedAsync` handler awaited to completion
before the next delivery), so an inbound delay doesn't just slow down the one delayed message — it
blocks that whole consumer from dispatching anything else while it waits. Watching
`vsaga.saga.OrderSaga`'s queue depth via the RabbitMQ management API showed it pinned at 32
unacked (its prefetch ceiling) and climbing in `messages_ready`, i.e. an unbounded backlog, not
steady-state throughput. Retuned to `Delay.MaxDelay = 4s` (committed value, in
`docker-compose.chaos.yml`) and the same queue drained to 0/0 within a couple of minutes and stayed
there — worth knowing before pointing any inbound-delay fault at a single-consumer subscription.

With the retuned config, a ~2-minute run produced 54 sagas: 25 `Completed`, 15 `TimedOut` (recovered
via `AwaitingPayment`'s compensation, confirmed per-saga via `GET /api/sagas/{id}/timeline`), 7
`Failed` (the participants' own normal business failures — declined card, out-of-stock, rejected
shipment), and 7 still `Running` (mostly just-submitted; a couple genuinely stuck in
`AwaitingInventory` with a dropped `InventoryReserved` and no timeout there to rescue them — the
honest gap called out above, not something this pass hides). One saga's timeline directly confirmed
`IsDuplicateAsync` dedup: a chaos-duplicated `OrderSubmitted` reached the orchestrator twice, and
`SagaStarted` was logged exactly once. Another showed `Duplicate` and `Drop`/`Delay` compounding
usefully: a duplicated `ReserveInventory` made `InventoryParticipant` reserve stock twice — at the
time, plain participants had no dedup of their own, only the saga orchestrator did (an honest
asymmetry this pass left as a known finding; since fixed, see "Timeout/message race fix" below) —
and of the two resulting `InventoryReserved` replies, chaos dropped one and delayed the other; the
delayed copy still got through and the saga proceeded normally — redundancy compensating for loss,
the textbook at-least-once story.

The more interesting catch: one saga's container log (`fail: SagaTimeoutDispatcherHostedService`,
`SagaConcurrencyException: ... was not at expected version 1; it was updated concurrently`),
cross-checked against that saga's `/timeline`, showed a delayed `PaymentCharged` arriving mere
seconds before its `AwaitingPayment` timeout was due. The
message-handling path and `SagaTimeoutDispatcherHostedService`'s independent poll both read the saga
at version 1 before either had written back, so both proceeded: the message path published
`ShipOrder` and drove the saga to `Completed`, while the timeout path — unaware it had lost the race
until its own final write — had *already* published `RefundPayment`/`ReleaseInventory` before that
write hit `SagaConcurrencyException` and was correctly rejected. The optimistic-concurrency check
stopped the saga from being corrupted into `Failed` after it had actually shipped, but it doesn't
retract side effects a losing branch already published — this order shipped **and** got refunded.
That's a real, pre-existing race between the timeout dispatcher and normal message handling
(distinct from the "concurrency-safe timeout claiming" fix, which only protects two dispatcher
instances from double-claiming the same timeout row, not a timeout from racing a message on the same
saga) — a genuine finding this pass surfaced but didn't fix, since fixing `SagaOrchestrator`'s
timeout/message race was out of scope for a fault-injection *package*. Left here rather than quietly
dropped, in keeping with how the rest of this README treats gaps chaos testing finds. **Fixed in a
later pass** — see [`timeout-message-race-fix.md`](timeout-message-race-fix.md).

`VSaga.Chaos.Tests` covers each fault type in isolation (trigger vs. no-trigger, both directions,
the no-double-ack property of duplicate-inbound, the `RollTrigger`/`NextDelay` probability helpers'
edge cases, and `AddVSagaChaos`'s registration gating) using the same hand-written-fake xUnit style
as the rest of the repo's tests — no mocking framework, `FakeTimeProvider` for the delay tests
instead of real waits.
