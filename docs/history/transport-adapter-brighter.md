# History: transport adapter, Brighter

> Preserved verbatim from the original `README.md`. Describes commit `c6f2307` ("Add Wolverine,
> MassTransit, and Brighter transport adapters"). See
> [`../transports/brighter.md`](../transports/brighter.md) for the current reference documentation.

---

## Transport adapter: Brighter

`VSaga.Transport.Brighter` (`Paramore.Brighter` + `Paramore.Brighter.MessagingGateway.RMQ.Async`
10.7.0, latest stable at the time of writing) implements `IMessageTransport` directly on Brighter's own
transport-level primitives — `RmqMessageProducer`'s `IAmAMessageProducerAsync.SendAsync` to publish, and
`RmqMessageConsumer`'s `IAmAMessageConsumerAsync` to receive/ack/reject — never Brighter's
`CommandProcessor` dispatch pipeline, its Outbox/Inbox, its request-handler routing, or any
workflow/scheduler feature. Same rule this repo already applies to RabbitMQ.Client directly
(`dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs:4-6`): vSaga never uses another bus's own
saga/state-machine machinery, only its wire-level publish/consume primitives.
`dotnet/src/VSaga.Transport.Brighter/BrighterTransport.cs:78-146` (publish) and `:148-232` (subscribe/consume)
are the whole adapter; `ServiceCollectionExtensions.AddVSagaBrighter` wraps it in the same
`MiddlewarePipelineTransport` every other adapter shares, so chaos/topology-recording middleware work
unchanged (`dotnet/src/VSaga.Transport.Brighter/ServiceCollectionExtensions.cs`).

**Constructed directly, not through Brighter's usual DI story.** Brighter is normally wired via
`services.AddBrighter(...).UseExternalBus(...)` plus a producer registry keyed by topic — but that helper
exists to wire up `CommandProcessor`'s dispatch/outbox stack, which this adapter must not depend on.
`AddVSagaBrighter` (`ServiceCollectionExtensions.cs`) instead registers `BrighterOptions` and
`BrighterTransport` as plain singletons and wraps the latter in `MiddlewarePipelineTransport` — the same
one-call shape as `AddVSagaRabbitMq`, at the cost of diverging from Brighter's own idiomatic setup.

**Two mechanical differences from `RabbitMqTransport`, both forced by what Brighter's gateway actually
exposes, not a design preference:**

- *Direct-to-queue `SendAsync` has no default exchange to target.* Brighter's `RmqMessageProducer` is
  bound to exactly one `Exchange` for its whole lifetime and always publishes using
  `Header.Topic.Value` as the routing key — there is no per-publish exchange override and no
  "default/nameless exchange" concept exposed anywhere in the package (confirmed by reflecting over
  `RmqMessagingGatewayConnection`, `RmqPublication`, and `RmqMessageProducer`'s constructors: none expose
  it). `RabbitMqTransport.SendAsync` targets AMQP's default exchange directly
  (`dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs:70-74`); that path doesn't exist here. Instead,
  `SubscribeAsync` binds the queue's own name as an *extra* routing key on the same topic exchange
  (`BrighterTransport.cs:160-164`), so a direct send just publishes with that routing key — one queue
  reached, mechanically different route, functionally identical outcome. `Send_DeliversDirectlyToNamedQueueWithoutExchange` passes either way.
- *One queue, many routing keys, needs the primitive under `IAmAChannelFactory`.* The higher-level
  `Subscription` config type that `IAmAChannelFactory`/`RmqSubscription` consume exposes a single
  `RoutingKey` property — it cannot express "bind this one queue to N routing keys," which is exactly
  what one consumer subscribed to several message types needs. `RmqMessageConsumer`'s own constructor
  can (it takes a `RoutingKeys` collection), so `SubscribeAsync` constructs it directly
  (`BrighterTransport.cs:166-171`) rather than going through `IAmAChannelFactory` — the "lowest-level
  primitive the Service Activator itself sits on," per this track's own scope notes.

**Pull-based consumption needs its own pump.** `IAmAMessageConsumerAsync.ReceiveAsync(timeout)` is
poll-based, unlike RabbitMQ.Client's push-based `AsyncEventingBasicConsumer` that `RabbitMqTransport`
wires up. `SubscribeAsync` runs its own background loop (`ConsumeLoopAsync`, `BrighterTransport.cs:194-205`)
playing the same role Brighter's own Service Activator message pump would — deliberately never brought
in, since it's part of the `CommandProcessor`/dispatcher stack this adapter must not depend on.

**A gotcha live-verification-adjacent testing caught, not the live pass itself this time: topology
declares lazily, on the first receive.** Direct testing against a live broker (constructing
`RmqMessageConsumer`/`RmqMessageProducer` from this package outside any test harness) showed a message
published before a fresh consumer's first `ReceiveAsync` call is silently dropped — the queue and its
bindings don't exist yet, because `RmqMessageConsumer.EnsureChannelAsync` declares them lazily inside
`ReceiveAsync` itself, not in the constructor. `IMessageTransport.SubscribeAsync`'s contract requires
topology to exist *before* the method returns (RabbitMqTransport's own doc comment says so explicitly:
"need to declare exchanges/queues/bindings ... before returning" —
`dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs:26-30`). `SubscribeAsync` forces that eagerly with
a 50ms warm-up receive before starting the consume loop (`BrighterTransport.cs:173-178`). Skipping it
reproduces the exact bug the sub-saga headers are supposed to prove don't exist: a header nobody actually
reads because the message carrying it was silently dropped before a real receive path ever touched it.

**Known gap: no unroutable-publish detection.** `RabbitMqTransport` publishes with `mandatory: true`
plus RabbitMQ.Client's native publisher-confirm tracking, so an unroutable message throws
`MessageTransportPublishException` deterministically
(`dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs:79-88`). `Paramore.Brighter.MessagingGateway.RMQ.Async`
10.7.0's `RmqMessageProducer` never sets that flag — confirmed both by inspecting its publish path
(`RmqMessageProducer`/`RmqMessagePublisher`'s constructors and properties expose no such option anywhere:
not on `RmqPublication`, not on `RmqMessagingGatewayConnection`) and by direct testing against a live
broker: publishing to a routing key nobody has ever bound a queue to still yields
`PublishConfirmationResult.Success = true`. The broker only ever refuses to route a message back
(`basic.return`) when the publish opts into mandatory delivery, which this package's producer does not do
and provides no way to request. `BrighterTransport.SendWithConfirmationAsync`
(`BrighterTransport.cs:115-146`) still wires up `ISupportPublishConfirmationAsync`'s confirmation event
and throws `MessageTransportPublishException` on `Success = false` — the one failure mode this package's
confirmation event can actually surface (a genuine broker-side nack, e.g. a queue at its length limit) —
but that is a strictly smaller net than RabbitMQ's mandatory-plus-confirms combination catches.
`dotnet/tests/VSaga.Transport.Brighter.Tests/BrighterTransportTests.cs`'s
`Publish_ToUnboundRoutingKey_DoesNotThrow_NoMandatoryReturnSupportInBrighterRmqGateway` documents this
verified behavior directly rather than asserting a throw that cannot occur.

**Header round-trip, the property that actually matters for sub-saga composition.** All four
`MessageEnvelope` headers (`SourceServiceHeader`, `CausationIdHeader`, `ParentSagaTypeHeader`,
`ParentCorrelationIdHeader`) ride in Brighter's `MessageHeader.Bag`
(`BrighterTransport.cs:98-103` outbound, `:267-280` inbound) — confirmed byte-for-byte by
`PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged`, the one test in this suite that a-c don't
exercise since they never set custom headers. `ReceivedMessage.Headers` is filtered to the `x-vsaga-`
prefix on the way in (`BrighterTransport.cs:270`) rather than passed through unfiltered the way
`RabbitMqTransport.ToStringHeaders` does: Brighter's `Bag` also carries its own CloudEvents-flavored
echoes of core header fields on receipt (`CorrelationId`, `Topic`, `HandledCount`, `cloudEvents_id`, ...)
that raw AMQP headers never have, and letting those round-trip forward through redelivery
(`SagaOrchestrator.HandleInfrastructureFailureAsync` rebuilds `envelope.Headers` from
`received.Headers` — `dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:70-74`) would carry Brighter-internal
noise as bogus outbound headers on every redelivered message. Every real vSaga header in this codebase
is `x-vsaga-`-prefixed with no exception, so the filter drops nothing that matters.

**Mutation-tested the same way the RabbitMQ adapter's own header handling gets no free pass on.**
Deliberately removing the `envelope.Headers` copy loop in `BuildOutboundMessage`
(`BrighterTransport.cs:98-103` at the time) reran all four tests: exactly
`PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged` failed
(`KeyNotFoundException: The given key 'x-vsaga-source-service' was not present in the dictionary`), the
other three stayed green. Reverting the break brought all four back to green. The three that don't set
custom headers genuinely can't catch a header-dropping bug — same lesson this repo already drew from the
`CausationId` header story in [`sub-saga-parent-linkage.md`](sub-saga-parent-linkage.md) above.

**Live-verified under docker compose**, project name `vsaga-brighter`, ports remapped via
`docker-compose.brighter.yml` (postgres `5445`, rabbitmq `5972`/`15972`, dashboard-api `5380`) to run
alongside two other adapter tracks' own concurrent stacks on the same machine. Brought up at
`2026-08-26T05:27:06Z`; postgres and rabbitmq reported healthy by `05:27:18Z`, dashboard-api by
`05:27:24Z`, order-processing by `05:27:30Z` — under 25 seconds end to end, cold (no prior image cache for
this track's Dockerfile layer additions). Traced `PostShipmentChoreography`'s `StartChildAsync` →
`InvoiceDeliverySaga` and `InvoiceFollowUpSaga`'s → `InvoiceArchivalSaga`, both real `StartChildAsync`/
initiating-message pairs over a live Brighter-mediated publish→receive round trip:

| Child saga | Child `CorrelationId` | `ParentSagaType` | `ParentCorrelationId` |
|---|---|---|---|
| `InvoiceDeliverySaga` | `4dc67113-4691-4c9a-bed5-7de609fd707a` | `PostShipmentChoreography` | `53b94350-a768-474f-a89c-02530ee2300d` |
| `InvoiceArchivalSaga` | `dac3551b-5dd7-4d66-8976-95b40c4b3885` | `InvoiceFollowUpSaga` | `53b94350-a768-474f-a89c-02530ee2300d` |

Both children's `ParentCorrelationId` (`53b94350-a768-474f-a89c-02530ee2300d`) resolves to a real parent
row: `OrderSaga`/`PostShipmentChoreography`/`InvoiceFollowUpSaga` all share that exact correlation id
(one order, observed three times), with final states `Completed`/`Invoiced`/`Archived` respectively — the
concrete proof that `ParentSagaTypeHeader`/`ParentCorrelationIdHeader` survived a real
`StartChildAsync` publish, a real Brighter `RmqMessageProducer.SendAsync`, a real broker round trip, and a
real `RmqMessageConsumer.ReceiveAsync`, landing correctly on `SagaState.ParentSagaType`/
`ParentCorrelationId` at instance-creation time — on this transport, not just in a Testcontainers unit
test. 27 `OrderSaga` instances were created in the same window; only the one traced above happened to
race its way to `InvoiceIssued` before the pass was torn down, which is expected given the sample's
built-in random failure rates and this adapter needing no changes to that timing.

**Open issue found during the live pass, not blocking.** `RmqMessageConsumer` on the two low-traffic
sub-saga queues (`vsaga.saga.InvoiceDeliverySaga`, `vsaga.saga.InvoiceArchivalSaga`) intermittently
logged `Paramore.Brighter.ChannelFailureException` / `precondition_failed: unknown delivery tag N` a
handful of times over several minutes, each time followed by RabbitMQ.Client's automatic connection
recovery reconnecting successfully within 1-5 seconds. This is consistent with the documented
RabbitMQ.Client limitation that a message delivered-but-unacked immediately before an automatic
connection/channel recovery cannot be acked afterward against the recovered channel's restarted delivery
tag numbering — `RabbitMqConnectionManager` enables the same `AutomaticRecoveryEnabled`/
`TopologyRecoveryEnabled` flags for `RabbitMqTransport`
(`dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqConnectionManager.cs:26-27`), so this class of issue isn't unique
to Brighter's gateway, just more likely to surface on a queue idle enough that a long-lived consumer
channel goes a while between real deliveries. `PollBatchAsync`'s existing catch-log-retry loop
(`BrighterTransport.cs:217-231`) already recovers from it automatically and no message was lost in this
pass — both traced sub-saga instances above were created correctly despite it — but it's reported here
rather than silently absorbed, since a livelier queue under sustained chaos-overlay load might surface it
more often than this pass's five occurrences.

**One more thing this pass caught.** `docker-compose.brighter.yml` uses the `!override` YAML merge tag on
`ports` because Compose's default list-merge behavior concatenates `ports` arrays across `-f` files
instead of replacing them — without it, this overlay would also try to bind `docker-compose.yml`'s
original host ports (5433/5672/15672/5080) alongside its own remapped ones, exactly the collision it
exists to avoid. `docker-compose.wolverine.yml` and `docker-compose.masstransit.yml` were written without
the tag and had the identical latent bug; both were fixed to match during integration.
