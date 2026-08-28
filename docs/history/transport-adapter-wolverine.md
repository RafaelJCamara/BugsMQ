# History: transport adapter, Wolverine

> Preserved verbatim from the original `README.md`. Describes commit `c6f2307` ("Add Wolverine,
> MassTransit, and Brighter transport adapters"), integrated in `49d029b` ("Make OrderProcessing
> sample's transport provider config-switchable") and `c413c4b` ("Relocate MiddlewarePipelineTransport
> into new BugsMQ.Transport.Common"). See [`../transports/wolverine.md`](../transports/wolverine.md)
> for the current reference documentation.

---

## Transport adapter: Wolverine

`IMessageTransport`'s own doc comment (`dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs:4-6`) names
Wolverine as a future adapter alongside MassTransit, on the same terms as RabbitMqTransport: use the
target bus's own raw send/receive primitives, never its native saga/state-machine or handler-discovery
machinery. `VSaga.Transport.Wolverine` is that adapter, built on WolverineFx 6.30.0 / WolverineFx.RabbitMQ
6.30.0 (latest stable on NuGet as of this pass — confirmed via the NuGet flat-container index, not
training-data memory) plus WolverineFx.RuntimeCompilation, which Wolverine 6.x now requires explicitly
(core no longer ships the Roslyn-based runtime compiler; referencing the package auto-registers it).

**The scope boundary, concretely.** Wolverine is fundamentally a mediator: its normal mode deserializes an
inbound envelope into a specific CLR type and invokes a `Handle(T)` method discovered by assembly
scanning at startup. vSaga's `SubscribeAsync` is the opposite shape — a runtime-registered
`(TransportSubscription, Func<ReceivedMessage, CancellationToken, Task>)` pair, created dynamically,
often several times, well after the host has already started. Routing a real saga message type through
Wolverine's own discovery would mean Wolverine owning dispatch to business logic, which is exactly what
the doc comment forbids. `RawEnvelope` (`dotnet/src/VSaga.Transport.Wolverine/RawEnvelope.cs`) is the fix: every
single piece of vSaga traffic — regardless of its real message type — is sent and received as this one
empty marker type, so Wolverine's handler discovery only ever has to know about one static
`RawEnvelopeHandler.Handle` method (`RawEnvelope.cs`), never a saga-specific type. The four vSaga headers,
the real message type name, the correlation id, and the message id all travel inside a small
self-describing JSON payload (`WireEnvelope`, `dotnet/src/VSaga.Transport.Wolverine/WireEnvelope.cs`) carried
verbatim as `Envelope.Data` — deliberately *not* relying on Wolverine's own `Envelope.Headers`-to-AMQP-property
mapping, so the header round trip is provably correct independent of whatever that mapping does.

**Publish/send: Wolverine's raw-send primitive, not its routing rules.** `WolverineTransport.PublishAsync`/
`SendAsync`/`PublishRawAsync` all funnel into `PublishInternalAsync`
(`WolverineTransport.cs:47`), which calls `IDestinationEndpoint.SendRawMessageAsync` (`WolverineTransport.cs:65`)
against a `RabbitMqEndpointUri.Topic(exchange, messageTypeName)` URI for publish/raw or
`RabbitMqEndpointUri.Queue(destination)` for a direct send — mirroring RabbitMqTransport's own
topic-exchange-vs-default-exchange split, just addressed through Wolverine's URI scheme instead of
`RabbitMQ.Client.IChannel.BasicPublishAsync`. `SendRawMessageAsync` puts exactly the bytes handed to it on
the wire; nothing about the real vSaga message type ever reaches Wolverine's own serializer.

**Subscribe: a dynamically-started listener, not a startup-declared one.** This is the one place Wolverine's
own docs stopped being useful and reflection on the actual 6.30.0 binaries had to settle it.
`IWolverineRuntime`'s own `RegisterListenerAsync`/`RemoveListenerAsync` extension methods
(`Wolverine.Runtime.WolverineRuntimeListenerExtensions`) looked like the obvious fit, but their XML doc
gives it away: "*persist as a registered listener that the cluster will activate on one node… within one
cluster assignment cycle (default 30s)*" — that's Wolverine's leader-elected, durability-store-backed
dynamic-multi-tenancy machinery, not an immediate single-node start, and it left every test hanging past
its 15s timeout with the listener simply never active. The actual fix,
`IEndpointCollection.StartListenerAsync`/`StopListenerAsync` (`WolverineTransport.cs:110`, `132`), starts a
listener on this node immediately, no durability store or cluster involved — `SubscribeAsync` calls it
directly against the `RabbitMqQueue` object `ModifyRabbitMqObjects` just declared
(`WolverineTransport.cs:89`), since `RabbitMqQueue` *is* a Wolverine `Endpoint`
(`RabbitMqQueue → RabbitMqEndpoint → Endpoint`, confirmed by walking the actual type hierarchy via
reflection, not the docs). Topology (one durable queue per consumer, bound to the shared topic exchange
per declared message type) is declared JIT inside that same call, through Wolverine's own
`IWolverineRuntime.ModifyRabbitMqObjects` object-management API — mirroring
`RabbitMqTransport.DeclareSubscriptionTopologyAsync`'s shape without ever touching `RabbitMQ.Client`
directly from this adapter.

**Ack/nack, without Wolverine's own retry fighting Core's.** Wolverine's model is implicit: return from
`Handle` and it acks; throw and its own error-handling policy decides what happens next. vSaga's model is
explicit: the caller of `SubscribeAsync`'s handler always calls `received.Ack.AckAsync`/`NackAsync` itself
before returning (see `SagaOrchestrator.HandleAsync`, `dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:39-50`).
`WolverineAckContext` (`dotnet/src/VSaga.Transport.Wolverine/WolverineAckContext.cs`) bridges the two: by the
time `RawDispatchRegistry.DispatchAsync` resumes after awaiting the downstream handler, one of Ack/Nack has
always already run, and a Nack is turned into a thrown exception so Wolverine's own `Handle` faults.
`ServiceCollectionExtensions.cs:46` configures `opts.OnException<Exception>().MoveToErrorQueue()` —
zero Wolverine-level retries, straight to its error queue on the first failure — because Core already owns
bounded, application-level redelivery (`SagaOrchestrator.HandleInfrastructureFailureAsync`,
`SagaOrchestrator.cs:52-90`, republishing via `PublishRawAsync` with an incremented
`x-vsaga-delivery-attempt` header) and never relied on broker-native requeue in the first place — see that
method's own doc comment. `NackAsync(requeue: false)` therefore only ever needs to mean "settle this as
rejected", exactly as the task brief anticipated.

**No Wolverine equivalent of RabbitMqTransport's unroutable-publish exception — confirmed, not assumed.**
RabbitMqTransport turns on `mandatory: true` plus publisher confirms and lets RabbitMQ.Client surface the
broker's `basic.return` as `MessageTransportPublishException.IsUnroutable`. WolverineFx.RabbitMQ 6.30.0
exposes publisher-confirm *settings* (`WolverineRabbitMqChannelOptions.PublisherConfirmationsEnabled` /
`PublisherConfirmationTrackingEnabled`) but never sets AMQP's `mandatory` flag and has no unroutable-return
handling anywhere — checked by scanning `Wolverine.RabbitMQ.dll` itself for `mandatory`/`Unroutable`/
`BasicReturn`: zero matches, and the shipped XML docs are equally silent. A message published to a routing
key nobody is bound to is therefore silently discarded by the broker. `Publish_ToUnboundRoutingKey_CompletesWithoutThrowing_NoWolverineUnroutableSignal`
(`dotnet/tests/VSaga.Transport.Wolverine.Tests/WolverineTransportTests.cs`) asserts that actual, verified
behavior instead of faking the RabbitMQ adapter's exception.

**Tests: 4/4 against a real broker, one new.** `WolverineTransportTests` mirrors
`RabbitMqTransportTests`'s Testcontainers-per-class shape (no mocks), adding the host-lifecycle setup
Wolverine's own hosted service needs (`services.AddWolverine` only actually opens a connection once
`IHost.StartAsync` runs its hosted services). `PublishAndSubscribe_DeliversMessageWithCorrelationAndType`,
`Send_DeliversDirectlyToNamedQueueWithoutExchange`, and the unroutable-publish test above pass unchanged in
spirit from the RabbitMQ suite; `PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged` is new and is
the one that actually proves the sub-saga headers round-trip — the other three never set a custom header at
all. All 4 pass: `Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 19s`.

**Verified by mutation.** Dropping `envelope.Headers` entirely when building the outbound `WireEnvelope`
(`WolverineTransport.cs`'s `PublishInternalAsync`) fails exactly one test —
`PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged`, with `KeyNotFoundException: The given key
'x-vsaga-source-service' was not present in the dictionary` — and leaves the other three green. Reverting
restores 4/4. That is the same "does the mutation break only the thing it should" bar the sub-saga slices
above were held to.

**Live verification**, tracing a real `StartChildAsync`/parent-linkage pair through Wolverine end to end,
under `docker compose -p vsaga-wolverine -f docker-compose.yml -f docker-compose.wolverine.yml up -d
--build` (host ports 5443/5772/15772/5180, chosen not to collide with the two other adapter tracks' worktrees
running concurrently on the same machine) with `Transport__Provider=Wolverine`:

- Container start at `2026-08-26T05:35:25Z`. `docker compose ... logs order-processing` shows normal saga
  traffic (inventory holds, payment charges/refunds, timeout scheduling) running entirely over
  `VSaga.Transport.Wolverine` seconds after start — no RabbitMQ.Client transport code in the loaded
  assembly graph at all for this run.
- Querying `SagaInstances` for rows created after the start timestamp: `PostShipmentChoreography` 4+,
  `InvoiceFollowUpSaga` 4+, `InvoiceArchivalSaga` 4+, `InvoiceDeliverySaga` 4+, `OrderSaga` 6+ (44 total by
  teardown) — both sub-saga pairs the task named are present.
- One concrete traced chain: `OrderSaga` correlation `3d415c9a-82ca-4370-9019-870a802775a8` reached
  `Completed`; the same correlation id's `PostShipmentChoreography` row (same id, per "Saga identity:
  (SagaType, CorrelationId)" above) reached `Invoiced` and, via `ctx.StartChildAsync`, started an
  `InvoiceDeliverySaga` with its **own fresh** correlation id `2d6c27f0-0cdd-4924-9041-aaed35b1d9a1` —
  and that child's `ParentSagaType`/`ParentCorrelationId` columns read back exactly
  `PostShipmentChoreography` / `3d415c9a-82ca-4370-9019-870a802775a8`. The other named pair
  (`InvoiceFollowUpSaga` → `InvoiceArchivalSaga`) shows the identical shape: correlation
  `107afb67-8b68-4f9f-bfc9-d31b967a2ef6`'s `InvoiceArchivalSaga` row points back to
  `InvoiceFollowUpSaga` / `3d415c9a-82ca-4370-9019-870a802775a8`. This is the concrete proof the four
  headers made it through a real publish→receive round trip on Wolverine, not just a unit test with a
  hand-built envelope.
- One inconsequential log line seen during startup, `Error: libgssapi_krb5.so.2: cannot open shared object
  file` — a Kerberos-auth-mechanism probe from the underlying client library on a Debian slim image with no
  Kerberos installed, unrelated to Wolverine and with no effect on any of the above (all 44 saga instances
  and every parent link resolved correctly).
- Torn down with `docker compose -p vsaga-wolverine ... down` (no `-v`, matching this repo's habit of
  leaving the volume between runs).

**Integration note, for the record.** This adapter was originally built in an isolated worktree that had
branched before the shared `VSaga.Transport.Common` relocation and the sample's `Transport:Provider`
switch existed, so that worktree worked around it by duplicating `MiddlewarePipelineTransport.cs` locally
and building its own version of the switch from scratch. Both were reconciled during integration into
`main`: the duplicate was deleted, `VSaga.Transport.Wolverine.csproj` now references the real
`VSaga.Transport.Common` project like every other adapter, and the `Wolverine` case was merged into the
one shared switch in `Program.cs` alongside MassTransit's and Brighter's. Rebuilt and re-verified — all 4
tests, the full 213-test solution suite, and a fresh `docker compose` pass — against the corrected
reference with no behavioral change.
