# History: transport adapter, MassTransit

> Preserved verbatim from the original `README.md`. Describes commit `c6f2307` ("Add Wolverine,
> MassTransit, and Brighter transport adapters"). See
> [`../transports/masstransit.md`](../transports/masstransit.md) for the current reference
> documentation.

---

## Transport adapter: MassTransit

`VSaga.Transport.MassTransit` (`dotnet/src/VSaga.Transport.MassTransit/`) is the second real
`IMessageTransport` adapter, alongside `VSaga.Transport.RabbitMQ`. Same contract
(`dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs:8-32`), same four methods, same
`MiddlewarePipelineTransport` wrapper — a different wire underneath. Pinned to **MassTransit
8.5.8** (`dotnet/src/VSaga.Transport.MassTransit/VSaga.Transport.MassTransit.csproj`), the latest 8.x
release confirmed on NuGet at the time of writing: MassTransit v9 is transitioning to a commercial
license, v8 remains Apache-2.0, and this adapter is built on it deliberately rather than on
whatever happened to be cached in training data.

**Built on MassTransit's transport, not its saga features — same boundary the doc comment already
states.** `IMessageTransport`'s own doc comment says concrete adapters "never use another bus's
native saga/state-machine features, only its transport"
(`dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs:4-6`). `MassTransitTransport` is built
entirely on `IBus`/`IPublishEndpoint`/`ISendEndpointProvider` for outbound and
`IConsumer<T>`/`ConsumeContext<T>` for inbound, over MassTransit's RabbitMQ transport — never
Courier (routing slips) and never Automatonymous/its own saga persistence. `SagaOrchestrator`
still owns every bit of retry, redelivery, and dedup (`dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:52-90`);
this adapter only moves bytes.

**One MassTransit contract for every vSaga message, not one per type.** MassTransit's own pub/sub
topology is built around compile-time generics (`Publish<T>`, `IConsumer<T>`), but
`TransportSubscription.MessageTypes` only ever hands `SubscribeAsync` a list of runtime `Type`
instances — the same mismatch `RabbitMqTransport` sidesteps by treating the RabbitMQ.Client body as
opaque JSON bytes. `VSagaEnvelopeMessage` (`dotnet/src/VSaga.Transport.MassTransit/VSagaEnvelopeMessage.cs`)
is the one fixed record every vSaga message actually travels as: `MessageTypeName` plus an
already-`System.Text.Json`-serialized body. `AddVSagaMassTransit`
(`dotnet/src/VSaga.Transport.MassTransit/ServiceCollectionExtensions.cs`) forces it onto one durable
topic exchange (`cfg.Message<VSagaEnvelopeMessage>(m => m.SetEntityName(...))`,
`cfg.Publish<VSagaEnvelopeMessage>(p => p.ExchangeType = "topic")`) and reads the routing key back
off the message itself (`cfg.Send<VSagaEnvelopeMessage>(s => s.UseRoutingKeyFormatter(ctx =>
ctx.Message.MessageTypeName))`) — the same shared-topic-exchange-plus-per-type-routing-key shape
`RabbitMqTransport` gets natively, reconstructed one layer up. `SubscribeAsync`
(`dotnet/src/VSaga.Transport.MassTransit/MassTransitTransport.cs`) turns off MassTransit's default
auto-bind-on-consume (`e.ConfigureConsumeTopology = false`) — left on, every subscriber's queue
would receive every vSaga message ever published, since they all share one contract — and instead
binds one `IRabbitMqReceiveEndpointConfigurator.Bind<VSagaEnvelopeMessage>` per declared message
type, each with that type's name as the routing key.

**The four envelope headers ride on MassTransit's own header pipeline, not inside the wrapper
record.** `CorrelationId`/`MessageId` are set as native `SendContext` fields; `SourceServiceHeader`,
`CausationIdHeader`, `ParentSagaTypeHeader`, and `ParentCorrelationIdHeader` — plus a redundant
correlation/message-id pair, the same defense-in-depth `RabbitMqTransport` applies — are set via
`SendContext.Headers.Set(key, value)` and read back via `ConsumeContext.Headers.GetAll()`. This
matters because it is real MassTransit metadata making the round trip, not payload data smuggled
through a field nothing but this adapter ever inspects — the same distinction that mattered for
`SourceService`/`CausationId` shipping once already with tests that hand-built the field and proved
nothing (see [`sub-saga-parent-linkage.md`](sub-saga-parent-linkage.md) above).

**Ack/nack, adapted rather than replicated.** MassTransit has no mid-flight equivalent of
RabbitMQ.Client's channel-level `BasicAck` — a consumer settles a delivery only by returning
normally (ack) or throwing (fault). `VSagaEnvelopeConsumer` bridges this onto
`IMessageAckContext`: `AckAsync`/`NackAsync` just record the caller's decision, and `Consume`
turns a recorded nack — or no decision at all — into a thrown exception once the handler
completes. Every receive endpoint is configured `UseMessageRetry(r => r.None())`, so a fault lands
straight in `{queue}_error` rather than being retried by MassTransit itself against
`SagaOrchestrator`'s own bounded-redelivery wishes (`HandleInfrastructureFailureAsync`,
`dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs:61-90`, never relies on broker-native requeue). No
poison-queue/DLX topology is replicated from `RabbitMqTransport` — the scope note calling that
defense-in-depth specific to that adapter, not a contract requirement, held up under actual
implementation.

**Unroutable publish.** MassTransit surfaces RabbitMQ's mandatory-publish-plus-return semantics as
`MessageReturnedException` (confirmed against MassTransit's own test suite,
`dotnet/tests/MassTransit.RabbitMqTransport.Tests/Mandatory_Specs.cs`) when `PublishContext.Mandatory` is
set and no queue is bound for the routing key. `MassTransitTransport` sets it on every publish and
wraps the exception into the same provider-agnostic `MessageTransportPublishException` RabbitMQ's
own adapter throws, `IsUnroutable` included.

**Tests: 4 against a real broker, no mocks** (`dotnet/tests/VSaga.Transport.MassTransit.Tests/`,
Testcontainers `rabbitmq:4-management`, mirroring `RabbitMqTransportTests`' own IAsyncLifetime
shape): publish-and-subscribe with correlation id and type preserved; direct send to a named queue
bypassing the exchange; unroutable publish throwing `MessageTransportPublishException`; and the new
`PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged`, which stamps all four vSaga headers
to distinct values and asserts every one survives a real publish→receive round trip byte-for-byte —
the one test of the four that actually exercises the header pipeline this section is about.

**Mutation-tested.** Commenting out the loop in `MassTransitTransport.ApplyEnvelope` that copies
`envelope.Headers` onto `SendContext.Headers` — the only line standing between the four vSaga
headers and MassTransit's wire — reran the suite: exactly
`PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged` failed
(`Header 'x-vsaga-source-service' did not survive the round trip at all.`), all three other tests
stayed green. Reverting the break brought all four back to green. The failure mode is exactly the
one this test exists to catch, and nothing else does.

**Live-verified under `docker compose`**, its own project namespace (`vsaga-masstransit`, remapped
host ports so it runs concurrently with the RabbitMQ and Wolverine tracks' own stacks on the same
machine — `docker-compose.masstransit.yml`), tracing a real `StartChildAsync`/`NotifyParentAsync`
pair end to end to prove the parent-linkage headers made it through an actual MassTransit
publish→receive round trip, not just the unit test above:

- Project `vsaga-masstransit`, host ports remapped per `docker-compose.masstransit.yml`
  (postgres 5444, rabbitmq 5872/15872, dashboard-api 5280) so this ran concurrently with the other
  two adapter tracks' own stacks on the same machine. Brought up at `2026-08-26T05:21:51Z` UTC;
  `docker compose -p vsaga-masstransit -f docker-compose.yml -f docker-compose.masstransit.yml up
  -d --build` finished image builds (both `order-processing` and `dashboard-api`, dependency
  restore + publish) and had every container healthy in under 15s of build time on top of an
  already-warm base-image cache.
- `order-processing`'s own startup log line confirms the real adapter, not a silent fallback to
  RabbitMQ: `info: MassTransit[0] / Bus started: rabbitmq://rabbitmq/`. No `MessageReturnedException`,
  no `MassTransitNackException`/`MassTransitDispatchException`, and nothing routed to a MassTransit
  `_error` queue across the whole run.
- 126 saga instances created after the startup timestamp: 37 `OrderSaga`, 22
  `PostShipmentChoreography`, 22 `InvoiceDeliverySaga`, 22 `InvoiceFollowUpSaga`, 23
  `InvoiceArchivalSaga`. 45 of those are children (`ParentSagaType IS NOT NULL`) — 22
  `InvoiceDeliverySaga` off `PostShipmentChoreography`, 23 `InvoiceArchivalSaga` off
  `InvoiceFollowUpSaga` — and a direct SQL check for `("ParentSagaType" IS NULL) <>
  ("ParentCorrelationId" IS NULL)` returns **0**: no half-linked rows, every child resolves to a
  real parent row.
- One pair traced end to end through `SagaEventLog`, correlation id
  `a8298edd-b616-4153-95a4-5214bf688a69` for the parent (`InvoiceFollowUpSaga`) and
  `4096c521-574e-454d-8758-1c5628ca1bd4` for the child (`InvoiceArchivalSaga`):
  child — `SagaStarted ArchiveInvoice` → `Requested`, `MessagePublished StoreInvoiceCopy` →
  `AwaitingStorage`, `MessageReceived InvoiceCopyStored` → `Archived`/`Completed`, with a
  `NotifyParentAsync`-published `InvoiceArchivalFinished` entry in between; parent —
  `SagaStarted InvoiceIssued` → `Requested`, a `ChildSagaStarted ArchiveInvoice` entry →
  `AwaitingArchival`, `MessageReceived InvoiceArchivalFinished` → `Archived`/`Completed`. Both
  transitions landed within the same ~330ms window (`05:22:48.58`–`05:22:48.91` UTC) — the fast
  path, no timeout involved — and the same correlation id widens out to the whole chain sharing
  `OrderSaga`'s id: `OrderSaga` Completed, `PostShipmentChoreography` Completed/`Invoiced`,
  `InvoiceDeliverySaga` (a sibling child under the same parent correlation id) Completed/`Delivered`,
  `InvoiceFollowUpSaga` Completed/`Archived`. This is the concrete proof the
  `x-vsaga-parent-saga-type`/`x-vsaga-parent-correlation-id` headers made it through a real
  MassTransit publish→receive round trip, read back by `SagaOrchestrator` into real
  `ParentSagaType`/`ParentCorrelationId` columns — not just the unit test above.
- Torn down with `docker compose -p vsaga-masstransit -f docker-compose.yml -f
  docker-compose.masstransit.yml down` (no `-v`, matching this repo's habit of leaving the volume
  between runs).

**Deviations from the brief:** `docker-compose.masstransit.yml`'s `order-processing.environment`
block adds one key beyond the exact block specified in the task
(`MassTransit__ConnectionString: "amqp://guest:guest@rabbitmq:5672/"`) — the base
`docker-compose.yml` already sets `RabbitMq__ConnectionString` for the default provider, but
nothing populates the "MassTransit" config section `MassTransitOptions` binds from, and without it
the adapter would default to `localhost:5672`, unreachable from inside the container network. Noted
rather than silently worked around.
