# vSaga

vSaga is an orchestration-first saga library for .NET 10, built directly on `RabbitMQ.Client`
(no MassTransit/Wolverine dependency). It gives you a fluent saga DSL, a persisted event log,
EF Core (Postgres) and in-memory persistence, an in-memory testing harness, OpenTelemetry
instrumentation, and a saga-type-agnostic ops dashboard (ASP.NET Core API + Angular SPA) with
live updates and manual retry.

There is no prior README in this repo's history — this document is the first one, written
alongside a production-hardening pass. It covers the project's current state and the decisions
made during that pass.

## Repository layout

```
src/
  VSaga.Abstractions             Contracts only: saga/transport/persistence interfaces, no implementation deps
  VSaga.Chaos                    Opt-in fault-injection transport middleware (delay/drop/duplicate)
  VSaga.Core                     The saga engine: fluent DSL + orchestrator runtime
  VSaga.Observability            OpenTelemetry hosting extensions
  VSaga.Persistence.EFCore       EF Core store implementations (provider-agnostic)
  VSaga.Persistence.EFCore.Postgres   Postgres-specific EF Core migrations (see "EF Core migrations" below)
  VSaga.Persistence.InMemory     In-memory store implementations (dev/test)
  VSaga.Testing                  SagaTestHarness for unit-testing saga definitions
  VSaga.Transport.Common         Shared IMessageTransport decorator (MiddlewarePipelineTransport)
  VSaga.Transport.InMemory       In-memory IMessageTransport (dev/test)
  VSaga.Transport.RabbitMQ       Real IMessageTransport over RabbitMQ.Client
  VSaga.Transport.Wolverine      IMessageTransport over WolverineFx.RabbitMQ
  VSaga.Transport.MassTransit    IMessageTransport over MassTransit 8.x + RabbitMQ
  VSaga.Transport.Brighter       IMessageTransport over Paramore.Brighter's RabbitMQ gateway
  VSaga.Dashboard.Api            ASP.NET Core API + SignalR hub for the ops dashboard
dashboard-web/                    Angular 21 SPA for the dashboard (list/detail, live updates)
samples/
  VSaga.Samples.OrderProcessing(.Contracts)   End-to-end reference saga + participants
tests/                            One test project per major src/ project
docs/                             Design sketches for work not yet built (see "Proposed work" below)
docker-compose.yml                Postgres + RabbitMQ + dashboard-api + the OrderProcessing sample
```

## Current state

Two commits exist before this pass (`a17f31d` "Add vSaga v1", `b1ff30b` "Add CI workflow and
expand test coverage"), plus an in-progress, previously-uncommitted code-quality pass (analyzers:
SonarAnalyzer.CSharp, Meziantou.Analyzer, AsyncFixer; `TreatWarningsAsErrors=true`; an expanded
`.editorconfig`) that this commit finishes and verifies alongside the production-hardening work
described below.

**What's implemented and tested:** orchestrated sagas (linear state machines with compensation,
step-level retry policies, per-state timeouts, manual whole-saga retry), choreographed sagas (see
"Choreographed saga support" below), EF Core/Postgres and in-memory persistence, four
`IMessageTransport` adapters — RabbitMQ (the reference implementation), Wolverine, MassTransit, and
Brighter, all over RabbitMQ-family brokers — plus the in-memory transport, the dashboard API/SPA, and
the `VSaga.Testing` harness. The OrderProcessing sample runs both saga kinds side by side against real
Postgres/RabbitMQ via `docker-compose.yml` — `OrderSaga` (orchestrated: compensation, timeouts) and
`PostShipmentChoreography` (choreographed: an independent fan-out tracked under the same correlation
id) — see "Choreography in the OrderProcessing sample" below. Its transport is config-switchable
(`Transport:Provider`, see `samples/VSaga.Samples.OrderProcessing/Program.cs`) to run against any of
the four adapters via a dedicated docker-compose overlay per adapter (`docker-compose.wolverine.yml`,
`.masstransit.yml`, `.brighter.yml` — RabbitMQ needs none, it's the default).

**The original v1 roadmap note is now fully closed.** All four items it deferred are built: additional
transport adapters (see the three "Transport adapter: ..." sections below — MassTransit and Wolverine
were the two the note named, Brighter shipped alongside them), parallel/fan-out saga steps ("Parallel
fan-out and join" below), and sub-saga composition: a parent can start a child, the relationship is
persisted and queryable ("Sub-saga composition: parent linkage" below), a child can report back to a
parent that actually waits for it ("Sub-saga composition: completion notification" below), and the
engine publishes a safety net for a child that fails or times out before it ever reaches its own
report-back step ("Sub-saga composition: engine safety net" below). The SignalR hub and polling
service, listed here as an untested gap through several passes, are covered as of "SignalR hub and
polling service tests". Nothing from that original note remains deliberately out of scope.

**Proposed work.** Every section of this README describes something that exists. Designs for work that
does *not* exist yet live under `docs/` instead, so the two are never confused. `docs/` currently has no
open proposals: [`docs/sub-saga-composition.md`](docs/sub-saga-composition.md) covered the whole of
sub-saga composition, and its last open piece — whether a parent's compensation cascades into its
children (Slice 3) — is now closed rather than proposed: considered, and deliberately not built, per the
doc's own recommendation. See "Sub-saga composition: parent linkage" below for where that's documented as
shipped (non-)behaviour, and the doc itself for the full reasoning, including one claim it made that
turned out to be wrong, one about `UnhandledEventPolicy.Throw` that also turned out to be wrong, and two
race conditions it didn't anticipate at all.

Chaos-engineering transport middleware, formerly listed here as a proposal, is implemented; see
"Chaos-engineering transport middleware" below. Additional transport adapters — the last genuinely open
item from the original v1 roadmap note — are also implemented; see the three "Transport adapter: ..."
sections below. `docs/` currently has no open proposals.

## Production-hardening pass (this commit)

A full-codebase gap analysis found several v1 gaps explicitly flagged in code comments as "out of
scope for v1" or simply absent. The following six were scoped in and are covered by both unit
tests and a live `docker-compose up` verification (real sagas processed end-to-end, migrations
applied cleanly, auth and health checks exercised over HTTP):

- **Dashboard API authentication** — a shared API key (`Dashboard:ApiKey` config), chosen over
  JWT/OIDC or basic auth as the right fit for a v1 internal ops dashboard with no existing identity
  infrastructure. `src/VSaga.Dashboard.Api/Auth/ApiKeyAuthenticationHandler.cs` checks the
  `X-Api-Key` header, then an `Authorization: Bearer` header, then falls back to the
  `?access_token=` query string. The SignalR JS client sends its `accessTokenFactory` token as an
  `Authorization: Bearer` header on the negotiate HTTP call, and only as the `?access_token=` query
  string on the actual WebSocket/SSE upgrade (which can't carry custom headers) — the handler needs
  all three to authenticate both legs of a hub connection. It **fails closed**: an unconfigured key
  denies every request rather than silently disabling auth. `/health` stays unauthenticated, per
  infra-probe convention. The Angular client sends the key via an `HttpInterceptorFn`
  (`dashboard-web/src/app/interceptors/api-key.interceptor.ts`) and the hub connection's
  `accessTokenFactory`. **Known limitation, accepted as part of this choice:** a key embedded in a
  compiled SPA bundle is visible via devtools — this closes off unauthenticated direct API access,
  it is not per-user auth.

- **Real `/health` check** — replaced the hardcoded `{ "status": "healthy" }` response with actual
  Postgres/RabbitMQ connectivity checks (`src/VSaga.Dashboard.Api/HealthChecks/`), returning `503`
  with a per-check breakdown when either is unreachable. Both checks resolve their dependency
  lazily via `IServiceScopeFactory` rather than a direct constructor dependency, so they degrade to
  "not configured" instead of failing to construct when a test host swaps out the real providers.

- **EF Core migrations** — replaced `EnsureCreatedAsync()` with versioned migrations
  (`db.Database.MigrateAsync()`). The Postgres-specific migration code lives in a new project,
  `VSaga.Persistence.EFCore.Postgres`, kept separate from `VSaga.Persistence.EFCore` so the
  latter stays provider-agnostic (its own stated design goal). Discovered and fixed during
  verification: the OrderProcessing sample independently calls its own `EnsureCreatedAsync()` at
  startup, which raced against the dashboard API's new migration on a fresh database. Fixed at the
  `docker-compose.yml` level — the dashboard API now has a real Docker `HEALTHCHECK` (using the
  `/health` endpoint above), and `order-processing` waits for it via `condition: service_healthy`
  before starting, so the migration always completes first.

- **RabbitMQ publisher confirms** — publishes now enable `PublisherConfirmationsEnabled`/
  `PublisherConfirmationTrackingEnabled` and `mandatory: true`, so a broker-side nack or an
  unroutable message throws `MessageTransportPublishException` instead of vanishing silently.

- **Concurrency-safe timeout claiming** — `EfCoreSagaTimeoutStore.ClaimDueAsync` now uses an
  atomic `UPDATE ... WHERE ... FOR UPDATE SKIP LOCKED ... RETURNING` on Postgres (verified under
  real concurrent load in `PostgresEfCoreStoreTests`), replacing the previous plain
  select-then-update that could double-claim a timeout across multiple dispatcher instances. Other
  providers (SQLite in tests) keep the original approach as a fallback.

- **Bounded orchestrator redelivery** — `SagaOrchestrator.HandleAsync`'s catch-all used to
  unconditionally requeue forever on any infrastructure-level failure (a deserialize error, a
  persistence-store exception — distinct from a saga step's own thrown exception, which was
  already handled correctly). It now redelivers with an incremented attempt-count header up to
  `SagaOrchestratorOptions.MaxDeliveryAttempts` (default 5), then routes to the dead-letter queue
  RabbitMQ already builds per consumer, logging a `SagaEntryType.DeliveryExhausted` timeline entry
  first.

Two incidental Dockerfile fixes surfaced only by building fresh (not from local caches), unrelated
to the above but necessary for the stack to build at all once `TreatWarningsAsErrors=true` took
effect: neither Dockerfile copied `.editorconfig` into the build context, so the analyzer rules it
relaxes (e.g. `MA0048`, deliberately disabled for this repo's intentionally-multi-type files) fell
back to their error-level defaults on a clean build.

## Saga Service Map

A third tab ("Map", alongside Timeline/Data) on the saga detail page renders an Azure-App-Map-style
service graph for that saga: nodes are the services involved (Initiator, Orchestrator, Participant,
or Unresolved), edges are the messages that flowed between them, plus a scrubber/replay animation
that steps through the saga's timeline at adjustable speed. `SagaMapBuilder`
(`src/VSaga.Dashboard.Api/SagaMapBuilder.cs`) is a pure, unit-testable function from a saga's raw
event log + a topology registry to nodes/edges/a replay script — no dependency on any specific saga
definition, consistent with the dashboard's saga-type-agnostic design.

Service identity travels on `MessageEnvelope` headers (`x-vsaga-source-service`,
`x-vsaga-causation-id`, both defined in `MessageEnvelope.From`); `SagaOrchestrator` reads these back
off the received message and stamps them onto `SagaLogEntry.SourceService`/`CausationId` for both
`SagaStarted` and `MessageReceived` entries. `SagaMapBuilder` stitches an edge by matching an
outbound entry's `MessageId` to a later inbound entry's `CausationId`; an outbound message with no
matching reply resolves its destination from the topology registry (`IServiceTopologyStore`,
populated by `TopologyRecordingTransport` observing real `SubscribeAsync` calls) — or renders as an
"unresolved" placeholder if even that doesn't know it — and is marked **unanswered** rather than
dropped, since a hung downstream service is often the most useful thing the map can show. Failure
detection covers two distinct shapes: a `StepFailed` entry (an action threw), and a business failure
reached through a normal, successful step transition with no exception at all (e.g. "payment
declined") — detected as the last inbound message before a `SagaCompleted` entry on a saga that ended
Failed/TimedOut.

Live verification against the real `docker-compose up` stack (not just unit tests with hand-seeded
`SagaLogEntry` objects, which pass even if the orchestrator never actually reads a header) caught two
real gaps: `MessageReceived`/`SagaStarted` entries were stamped with `SourceService` but never
`CausationId`, so nothing ever stitched a reply back to its request; and the business-failure-without-
exception case above had no detection path at all until added.

## Dashboard list: pagination, sorting, and a SignalR live-updates fix

**SignalR hub negotiate returning 401.** Live updates (the saga list refreshing, a saga detail page's
Map/Timeline re-fetching) were silently broken — the hub connection never completed, so the UI only
ever showed what it had at page load. Root cause: `ApiKeyAuthenticationHandler` originally only
checked the `X-Api-Key` header and the `?access_token=` query string, but SignalR's JS client sends
its `accessTokenFactory` token as an `Authorization: Bearer` header on the negotiate HTTP call, and
only falls back to the query string for the actual WebSocket/SSE upgrade (which can't carry custom
headers). Fixed by adding the `Authorization: Bearer` check (see "Dashboard API authentication"
above); verified live via the browser's own network/console logs — negotiate returns `200` and the
WebSocket connects, e.g. `WebSocket connected to ws://localhost:5080/hubs/saga?...&access_token=...`.
Regression-covered by `HubNegotiate_*` tests in `ApiKeyAuthTests.cs`, which the auth handler
previously had no coverage for at all (only `/api/sagas` was tested).

**Pagination.** The saga list previously hardcoded `page: 1, pageSize: 50` with no way to reach
anything past the first 50 sagas. `saga-list.ts` now tracks `page` and a selectable `pageSize`
(25/50/75/100, default 25) as signals, with Previous/Next controls disabled appropriately
(`page() * pageSize < totalCount()`). Any filter or page-size change resets back to page 1. A live
SignalR update for a saga not already in the loaded page only prepends into view while on page 1;
elsewhere it just bumps `totalCount` and surfaces a "N new sagas — Refresh" banner, rather than
silently showing the wrong rows for the page the user is looking at.

**Sortable Status/Updated columns.** Clicking either column header sorts — first click ascending,
second click reverses, switching columns resets to ascending. This was initially implemented as a
client-side sort over whichever page happened to already be loaded, which turned out to be a bug:
clicking a header only ever reordered the current page, so page 2+ kept showing rows in their
original server order. Fixed by pushing the sort down to the backend: `SagaListFilter` gained
`SortBy`/`SortDescending`, both `EfCoreSagaSummaryReader` and `InMemorySagaStore` apply the ordering
before `Skip`/`Take` (ties broken by `UpdatedAtUtc` descending, so paging through a sort stays
stable), and `GET /api/sagas` accepts `sortBy`/`sortDescending` query params. Status sorts by domain
progression (Running → Completed → Failed → Compensating → Compensated → TimedOut → Cancelled), not
alphabetically — the enum's declared order already matches, so `ORDER BY` on the stored int column
does the right thing for free. Changing the sort resets to page 1, like a filter change. The one
piece that legitimately stays client-side: keeping a live-pushed SignalR update inserted/repositioned
correctly within the already-loaded page between refetches, instead of always prepending regardless
of the active sort. Regression-covered by endpoint tests that split a sorted result set across two
pages to prove the ordering isn't just being reapplied to whatever page was requested, plus a
Testcontainers-backed Postgres test verifying the EF Core translation.

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
"Timeout coverage for every awaiting state" below; all three awaiting states now carry a timeout.

**Wiring.** `samples/VSaga.Samples.OrderProcessing` calls `AddVSagaChaos` only when
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
later pass** — see "Timeout/message race fix" below.

`VSaga.Chaos.Tests` covers each fault type in isolation (trigger vs. no-trigger, both directions,
the no-double-ack property of duplicate-inbound, the `RollTrigger`/`NextDelay` probability helpers'
edge cases, and `AddVSagaChaos`'s registration gating) using the same hand-written-fake xUnit style
as the rest of the repo's tests — no mocking framework, `FakeTimeProvider` for the delay tests
instead of real waits.

## Timeout/message race fix

Closes the race the chaos-testing pass above found but deliberately left unfixed. In
`SagaOrchestrator<TState>.HandleTimeoutAsync` (`src/VSaga.Core/Runtime/SagaOrchestrator.cs`), a due
timeout used to call straight into the saga definition — which is what runs `.Compensate()`/`.Publish()`
side effects — *before* persisting anything. If a normal message read the same snapshot version
concurrently (the exact "reply landed mere seconds before the timeout" scenario above), the timeout's
side effects went out over the transport regardless of whether its own subsequent persist then lost
the optimistic-concurrency check against that message's write.

The fix: `HandleTimeoutAsync` now claims the timeout with a version-checked persist *before* calling
into the saga definition at all, reusing the exact same `SagaConcurrencyException` mechanism that
already protected the final write — just moved earlier. A stale timeout is now detected and abandoned
before anything can be published, not after. A second, narrower window remains — a concurrent write can
still land between the claim succeeding and the timeout's own final persist, since real
`Compensate()`/`Publish()` I/O (and any step-level `RetryPolicy` delay) sits in between, and that
persist has no further claim to fall back on — every design considered for this fix (a version recheck
just before publishing, a claim-then-persist split, or folding claim-and-persist into one envelope)
shares this same residual limitation, since none of them serialize against a write landing mid-step
without a lock or an outbox-style deferred-publish redesign, both bigger changes than this fix's scope.
What changed for that narrower case: it's now caught and logged distinctly
(`"...lost a second race after its Compensate()/Publish() side effects already ran..."`) instead of
propagating an uncaught `SagaConcurrencyException` out to `SagaTimeoutDispatcherHostedService`'s
generic catch-and-log, which is what happened before this fix for *any* race, wide or narrow.

`tests/VSaga.Core.Tests/SagaOrchestratorTimeoutRaceTests.cs` covers both windows deterministically —
same controlled-fake technique as `SagaOrchestratorInfrastructureFailureTests`, decorating the
snapshot store to inject a concurrent reply synchronously at the exact call site that matters, rather
than relying on real timing. One test proves the pre-claim race no longer publishes any compensation
side effect; the other documents the accepted post-claim leak and proves it now fails gracefully
instead of throwing uncaught.

Verified live against the real chaos-enabled docker-compose stack with the fix applied: a ~20-minute
run processed 190 sagas (88 `Completed`, 37 `Failed`, 27 `TimedOut` via `AwaitingPayment` compensation,
38 still `Running`) with zero sagas that were both `Completed` and had a `RefundPayment`/
`ReleaseInventory` in their timeline, and zero `SagaConcurrencyException` anywhere in the logs — the
exact race window that surfaced the original bug is narrow enough that this run's real chaos timing
didn't happen to land in it (consistent with the original catch needing its own dedicated pass to
find), but the deterministic tests above force both interleavings directly rather than depending on
that timing.

Two secondary gaps the original chaos pass also flagged got picked up here:

- **Participant-level dedup.** `InventoryParticipant`/`PaymentParticipant`/`ShippingParticipant` had no
  idempotency guard of their own, unlike the saga orchestrator's `IsDuplicateAsync` — a chaos-duplicated
  command (or a genuine broker at-least-once redelivery) ran its business side effect twice, as the
  `Duplicate`+`Drop`/`Delay` finding above shows for `ReserveInventory`. Fixed with a small, bounded,
  process-local `MessageId` dedup guard added once to the shared `ParticipantService` base
  (`samples/VSaga.Samples.OrderProcessing/Participants/ParticipantService.cs`), covering all three
  participants — not durable across restarts, which is an honest limitation of its own, but enough to
  absorb the near-immediate redelivery chaos testing (and a real broker) actually produces.
- **`AwaitingInventory`/`AwaitingShipment` have no `WithTimeout`.** Left as-is, deliberately: the
  "States without a configured timeout" paragraph above already documents this as an intentional
  choice — chaos testing surfacing it as a real, honest gap rather than something to quietly patch in
  the same pass that found it. Revisit only if that framing should change. **The framing changed** —
  see "Timeout coverage for every awaiting state" below.

## Choreographed saga support

Closes the gap the original v1 commit flagged and left open: `SagaKind.Choreographed` existed as an
enum value (the persistence layer, EF Core migrations, and the dashboard's list/filter/badges already
supported it end-to-end) but nothing could actually produce an instance of one — there was no DSL, and
`VSaga.Core.Dsl.OrchestratedSagaDefinition<TState>` was the only `ISagaDefinition<TState>`
implementation in the codebase.

**What was added:** `VSaga.Core.Dsl.ChoreographedSagaDefinition<TState>`, a second fluent DSL base
class alongside the orchestrated one. Investigating the existing engine first showed that
`SagaOrchestrator<TState>`, `SagaRuntime<TState>`, `ServiceCollectionExtensions.AddSaga<TDefinition,
TState>()`, and `VSaga.Testing.SagaTestHarness` are all already written purely against
`ISagaDefinition<TState>` — none of them know or care what `Kind` a saga is. That meant this feature is
entirely additive to `VSaga.Core.Dsl`; the runtime, persistence, retry dispatcher, timeout dispatcher,
and dashboard needed **zero** changes, and a choreographed saga is registered with the exact same
`services.AddVSagaEngine(o => o.AddSaga<TDefinition, TState>())` call an orchestrated one uses.

**The actual design difference** is what a choreography *is*: there's no central conductor deciding
"what happens next", so `ChoreographedSagaDefinition` registers reactions per event type only —
`On<TEvent>()` — never gated to the instance's current recorded state the way
`OrchestratedSagaDefinition`'s `During(state).When<TEvent>()` gates its steps. Concretely:

- Any registered event can be observed while the saga instance is in *any* state, because independent
  participants — not this definition — decide what to publish and when. Two events published by two
  unrelated services have no reason to arrive in a "declared" order, so nothing in dispatch should
  assume one.
- `.RecordState(state)` replaces orchestration's `.TransitionTo(state)` — same underlying field
  (`SagaState.CurrentState`), renamed to be honest that it's a milestone label for the
  dashboard/timeline and for keying `Compensate(...)`/`WithTimeout(...)`, not a gate: nothing about this
  DSL's own dispatch depends on it.
- More than one event type can call `.StartsNewInstance()` (analogous to orchestration's
  `InitiatingMessageTypes`, but not derived from "the initial state's registered steps" the way
  orchestration derives it, since choreography has no per-state step table to derive it from). There is
  no single designated first step — whichever participant happens to publish first is the one that
  starts tracking.
- `Then`, `Publish`, `Send`, `Retry`, `Finalize`, `Compensate`, `CorrelateBy`, `WithTimeout`, and
  `OnUnhandledEvent` all carry the same meaning as they do for an orchestrated saga — compensation,
  timeout, and retry are all keyed off `CurrentState`/`VisitedStates` strings, not the gating mechanism,
  so none of that had to change.

**Shared internals, so the two DSLs can't silently drift.** The step-level retry loop and the
most-recent-first compensation walk are subtle enough (backoff timing, one failing compensation not
abandoning the rest) that duplicating them risked the two kinds quietly behaving differently over time.
Both are now factored into `VSaga.Core.Dsl.StepExecutor`/`CompensationRunner`, and
`OrchestratedSagaDefinition` was refactored to call them too (behavior-preserving — the full existing
test suite passes unchanged). `TimeoutBuilder<TState>` was changed to take a compensation-runner
delegate instead of the concrete orchestrated `SagaDefinitionModel<TState>`, so it's now shared as-is by
both DSLs' `WithTimeout(...)`. The two DSLs' public fluent builders (`EventBuilder` vs.
`ChoreographyEventBuilder`) were deliberately kept separate rather than unified behind a common
abstraction — the state-gated chaining `During(...).When<T>()` needs is orchestration-specific, and
forcing it into a shared shape would have leaked that gating concept into choreography's builder.

**Test coverage:** `tests/VSaga.Core.Tests/TestShippingChoreography.cs` (a fixture) and
`ChoreographedSagaTests.cs` (7 tests) run the new DSL through the real `SagaOrchestrator<TState>` and
in-memory transport/persistence, the same way `SagaOrchestratorTests.cs` covers the orchestrated DSL.
Beyond the compensate/timeout/retry parity checks, two tests specifically target what makes a
choreography different from an orchestrated saga rather than just re-proving shared plumbing:
`ReversedEventOrder_BothEventsStillHandled_BecauseDispatchIsNotGatedByCurrentState` publishes the same
two events in the reverse of their "natural" business order and shows both are still handled — which
`OrchestratedSagaDefinition` could not do without separately declaring a handler for each state the
event might arrive in — and `MultipleEventTypesCanIndependentlyStartANewTrackedInstance` shows a second,
non-"first" event type creating a new instance on its own. A happy-path test also confirms
`SagaSummary.Kind` round-trips as `SagaKind.Choreographed` through the real snapshot store and
`ISagaSummaryReader`, closing the loop on the `Choreographed`-kind fixtures `EfCoreStoreTests`/
`PostgresEfCoreStoreTests`/`SagaEndpointsTests` already had (those tested that the persistence/dashboard
layer could *store and filter* a `Choreographed` row; nothing previously exercised producing one for
real).

**Known limitation, found while scoping the sample wiring, left undone rather than silently patched
(same honest-gap convention as the rest of this README):** `CorrelationId` is a global 1:1 key to
exactly one saga instance across the *whole* engine, not per-saga-type — `SagaInstanceEntity`
(EF Core) and `InMemorySagaStore` both key a snapshot by `CorrelationId` alone, with no `SagaType` in
the key. That means a choreographed saga can't passively "listen in" on another saga's (orchestrated or
choreographed) correlation id — e.g. a `ChoreographedSagaDefinition` subscribing to `OrderProcessing`'s
existing `ShipOrder`/`OrderShipped`/`ShipmentFailed` messages, which all carry `OrderSaga`'s own
correlation id, would collide with `OrderSaga`'s already-existing row the moment it tried to
`InsertAsync` its own tracking instance under that same id (`SagaAlreadyExistsException`). This is why
no choreographed saga was wired into the `OrderProcessing` sample this pass: doing it honestly would
need a genuinely independent choreographed process — participants that mint/propagate their own
correlation id rather than reusing an existing orchestrated saga's — which is a bigger, separate change
than adding a DSL. The DSL itself has no such restriction; it's a property of the shared correlation-id
keyspace every saga (of either kind) already lives in.

> **Resolved in the next pass** — see "Saga identity: (SagaType, CorrelationId)" below, which makes the
> composite key real end to end. The follow-on it names (actually wiring a choreographed saga into the
> `OrderProcessing` sample) is still open.

## Saga identity: (SagaType, CorrelationId)

Closes the limitation the choreographed-saga pass documented directly above. A saga instance is now
identified by the pair `(SagaType, CorrelationId)` rather than by a correlation id alone, so two saga
types may track the same business transaction — which is precisely what lets a choreographed saga
observe a flow an orchestrated saga is already running.

**This is a breaking change to the public store contracts and the dashboard's URLs.** There is no
compatibility shim: this is a pre-1.0 library, and quietly keeping a "look up by correlation id alone"
path would have preserved exactly the ambiguity the change exists to remove.

**What moved.** `SagaInstances`' primary key became `(SagaType, CorrelationId)`, and every per-instance
read grew a leading `sagaType` parameter: `ISagaSnapshotStore<TState>.FindAsync`,
`ISagaEventLogStore.GetTimelineAsync`/`IsDuplicateAsync`, `ISagaTimeoutStore.CancelAsync`,
`ISagaAdminStore.ResetStateAsync`, `ISagaSummaryReader.GetAsync`/`GetDataJsonAsync`, and
`ISagaChangeNotifier.TimelineEntryAddedAsync`. `InsertAsync`/`UpdateAsync`/`AppendAsync` were left
alone — they already receive a `SagaState`/`SagaLogEntry` carrying its own `SagaType`.
`ISagaTimeoutStore.ScheduleAsync` had its first two parameters swapped purely for consistency, since it
was the one member that already took both and took them in the other order. The saga exceptions all
carry the saga type now, so their messages name an instance unambiguously. `SagaMapBuilder` needed no
change at all: it was already a pure function of a `SagaSummary` plus a pre-fetched timeline.

**Three of these were live correctness bugs, not just a missing feature.** Had a second saga type ever
started sharing a correlation id under the old code, then beyond the obvious
`SagaAlreadyExistsException` on insert:

- **Compensation would have run for states the saga never visited.**
  `SagaOrchestrator.GetVisitedStatesAsync` derives the compensation set from
  `GetTimelineAsync(correlationId)`. An unscoped timeline merges both sagas' entries, so one saga's
  `VisitedStates` would include the other's states — and `Compensate(state, ...)` is keyed on exactly
  those strings.
- **A broadcast message would have been silently swallowed.** `IsDuplicateAsync(correlationId,
  messageId)` is the idempotency check. The same message legitimately reaches several saga types; the
  second one to process it would have discarded its own *first* delivery as a duplicate.
- **One saga would have cancelled another's timeout.** State names are unique only within a saga type,
  so an unscoped `CancelAsync(correlationId, forState)` reaches across into a same-named state
  belonging to a different saga.

Each of these is now pinned by a test that fails against the unscoped query — verified by mutation,
not assumed: reverting the `SagaType` predicate in `EfCoreSagaEventLogStore` and
`EfCoreSagaTimeoutStore` fails exactly `TimelineAndDuplicateCheck_AreScopedToOneSagaType` and
`CancelTimeout_DoesNotCancelAnotherSagaTypesSameNamedState` and nothing else.

**Dashboard URLs.** Every per-instance route gained a saga-type segment:
`GET|POST /api/sagas/{sagaType}/{correlationId}[/timeline|/map|/retry]`. The list route
(`GET /api/sagas`) and `GET /api/saga-types` are unchanged. The Angular route became
`/sagas/:sagaType/:id`. SignalR's per-saga group is now `saga:{sagaType}:{correlationId}`, and
`TimelineEntryAdded` carries the saga type as a leading argument — without that, a detail view open on
one saga would receive the other's timeline entries.

A new `GET /api/correlations/{correlationId}` returns *every* saga instance tracking a correlation id.
That is the one place a bare correlation id is still a legitimate input: it's how a caller holding only
an id (an old bookmark, a log line, a support ticket) resolves it to a concrete instance. It returns a
list rather than a single summary precisely because the answer can now be more than one. It's mounted
at its own top-level path rather than `/api/sagas/by-correlation/{id}`, which would have sat in the same
route slot as `{sagaType}` and relied on literal-beats-parameter precedence to disambiguate.

> **Now surfaced in the dashboard** — the saga detail page resolves its correlation id through this
> endpoint, drops its own instance, and renders the rest as an "Also tracking this correlation id" strip
> linking to each sibling. Nothing renders in the ordinary one-saga case. Deliberately a snapshot rather
> than live: the detail page joins only its own instance's hub group, so a sibling's status change isn't
> pushed to it; the strip is refreshed whenever this saga itself updates, the same compromise the map tab
> already makes. Added in the pass that shipped the sample choreography, which is what first made a
> second saga per correlation id something you could actually click through to.

**Migration.** `20260825045219_ScopeSagaIdentityToSagaTypeAndCorrelationId` swaps the primary key and
re-leads the two `SagaEventLog` indexes with `SagaType`. No data migration is needed: `SagaType` was
already non-null on every row, and correlation ids were globally unique under the old key, so no
existing row can collide under the new one. A plain `CorrelationId` index is added to `SagaInstances`
to serve the new resolve-by-correlation-id lookup, which the composite key can't answer (its leading
column is `SagaType`).

**Still open, for the same reason as before:** no choreographed saga is wired into the `OrderProcessing`
sample yet. The keyspace no longer blocks it, so what remains is genuine sample design — deciding what
an independent choreographed process over these messages should actually be — rather than a constraint
of the engine. The engine-level capability is covered by `SagaIdentityScopingTests`, which runs an
orchestrated and a choreographed saga in one engine under a single shared correlation id.

> **Done in the next pass** — see "Choreography in the OrderProcessing sample" below.

## Choreography in the OrderProcessing sample

Closes the last item the two preceding sections left open. The sample now runs both saga kinds side by
side in one process, against one database, under one correlation id per order.

**The process.** Once `ShippingService` publishes `OrderShipped`, three further services —
`NotificationService`, `LoyaltyService`, `InvoicingService` — each react on their own initiative and
announce what they did (`CustomerNotified`, `LoyaltyPointsAwarded`, `InvoiceIssued`). Nothing commands
them: the new contracts are all events, with no matching `Do-X` command, which is the structural
difference from every other leg of this sample. `PostShipmentChoreography` observes that fan-out and
decides when the leg is finished. It commands none of the three.

> **Amended by a later pass.** "It publishes nothing" stopped being true when
> `PostShipmentChoreography` gained a `StartChildAsync` on its `InvoiceIssued` branch — see "Sub-saga
> composition: parent linkage" below. It still commands none of the three fan-out services and still
> waits on nothing it started; the join described here is unchanged.

**It shares `OrderSaga`'s correlation id**, because it is the same business transaction. The three
participants propagate the inbound correlation id onto their replies via `MessageEnvelope.From`, so
their events land on the tracker without anyone minting a new id. Both sagas therefore appear together
under `GET /api/correlations/{id}`. Two things had to already be true for this to work, both of them
from the identity pass above: the composite `(SagaType, CorrelationId)` key, and per-saga-type dedupe —
`OrderShipped` is delivered to `OrderSaga` and to `PostShipmentChoreography` alike (one queue per
subscription bound to a topic exchange), and under the old correlation-id-only `IsDuplicateAsync` the
second saga to see it would have discarded its own only copy.

**This surfaced a real gap in the choreography DSL, which is the interesting part.** A fan-out/join is
*the* characteristic choreography shape, and `Finalize(SagaStatus)` could not express its ending. With a
fixed status the only options were to nominate one branch as the finisher — wrong, because three
independent publishers have no fixed order and the nominated one may well land first — or never to
complete at all. So `ChoreographyEventBuilder` gained an overload:

```csharp
.Finalize(state => state.CustomerNotified && state.PointsAwarded && state.InvoiceIssued
    ? SagaStatus.Completed
    : null)   // null = handled, but not terminal yet
```

Registered identically on all three branches, this makes whichever branch arrives last the one that
completes the saga, without any branch assuming it is last. It is evaluated *after* the step's own
actions, so the branch that sets the final flag sees it. `StepDefinition.ResolveFinalStatus` is the one
place the fixed and conditional forms are collapsed, so the orchestrated and choreographed DSLs cannot
drift on which wins — the same reason `StepExecutor` and `CompensationRunner` are shared. The overload
is deliberately *not* added to the orchestrated `EventBuilder`: an orchestrated saga gates steps by
current state, so a conditional ending is already expressible there as separate `During(...)` branches.

`ChoreographyFanOutJoinTests` pins this across all six arrival orders. Verified by mutation rather than
assumed: replacing the selector with a fixed `Finalize(SagaStatus.Completed)` on one nominated branch
fails exactly the four orders in which that branch is not last, and passes the two in which it is.

**A subtlety the sample documents in place:** every non-terminal milestone registers its own timeout,
not just the first. Timeouts are keyed on `CurrentState` and the orchestrator cancels the pending one
whenever the saga transitions away, so a single `WithTimeout(AwaitingFulfilment, ...)` would be silently
cancelled by the first branch to report and could only ever catch an order where *nothing* came back —
leaving a saga stalled at two-of-three to hang forever.

**Not unit-tested at the sample level, by existing convention:** `tests/` holds one project per `src/`
project and none for `samples/`, so the sample's own wiring is verified live rather than in xUnit. What
*is* unit-tested is the engine capability behind it, via a reduced fixture (`TestFanOutChoreography`)
with the same shape.

**Live verification** against `docker compose up`: both types appear in `/api/saga-types`
(`OrderSaga` Orchestrated, `PostShipmentChoreography` Choreographed); the three services' events
genuinely interleave across orders (loyalty-first, invoice-first and notify-first all observed in one
run); every choreography instance reached `Completed`, and their terminal `CurrentState` varied between
`Invoiced` and `PointsAwarded` — direct evidence that *different branches finished last* and the join
handled each. For a single order, `/api/correlations/{id}` returned both sagas, their timelines stayed
separate (14 vs 13 entries, neither containing an entry belonging to the other), and `OrderShipped`
appeared as an inbound message in both.

## SignalR hub and polling service tests

Closes a gap the "out of scope for v1" note above had carried since the first commit: neither
`SagaHub`, `SignalRSagaChangeNotifier`, nor `SagaChangePollingService` had a single test. That was
tolerable while the hub's contract was stable. It stopped being tolerable once the saga-identity pass
changed `SubscribeToSaga` from `(correlationId)` to `(sagaType, correlationId)` and renamed the
per-saga group to `saga:{sagaType}:{correlationId}` — a regression to the old shape would have
compiled, passed CI, and broken live updates on every detail page at runtime. That change was verified
by driving a real hub connection by hand against the running stack, which is not something CI repeats.

**Coverage.** `SagaHubTests` pins the group-name format and the subscribe/unsubscribe membership
contract, including that two saga types sharing a correlation id join two distinct groups and that
leaving one doesn't remove the connection from the other's. `SignalRSagaChangeNotifierTests` pins the
in-process path: `SagaUpdated` reaches both the list group and the instance group, `TimelineEntryAdded`
reaches only the instance group and carries the saga type as a payload argument (the client filters on
it before appending). `SagaChangePollingServiceTests` covers the cross-process path that actually
delivers live updates in the deployed topology — sagas run in the OrderProcessing process, so the
notifier never fires in the dashboard and this diff-and-push loop is the only route.

**One small production change, for testability.** `SagaChangePollingService`'s tick body was extracted
into `PollOnceAsync(since, ct)`, returning the new watermark; `ExecuteAsync` is now just the timer loop
and its error handling. The alternative was a test that advances a clock and races a background task's
continuation. The class stays `internal` — it is composition-root wiring, not API surface — and the
test project reaches it through `InternalsVisibleTo` rather than being promoted to public.

Extracting it also made one thing explicit that was previously implicit: the watermark advances only
*after* the pushes succeed, so a tick that throws leaves it untouched and the next tick retries the same
window instead of skipping past it.

**Verified by mutation, not assumed.** Dropping the saga type from `GroupForSaga` fails six tests across
all three files; loosening the watermark comparison from `>` to `>=` fails exactly the two poller tests
that pin that boundary. Nothing else moves in either case.

## Timeout coverage for every awaiting state

Reverses a choice this README had defended across two passes: `AwaitingInventory` and
`AwaitingShipment` deliberately had no `WithTimeout`, documented as an honest gap chaos testing had
surfaced rather than something to quietly patch. The note ended "revisit only if that framing should
change." It changed, for a reason the data made hard to argue with: at the time of this pass the
database held **60 sagas stuck `Running` forever** — 43 in `AwaitingInventory`, 17 in
`AwaitingShipment` — each one an order holding inventory, and in the shipment case a charged card, with
no path back. "Documented gap" stops being the honest framing once it has a body count.

All three awaiting states now share one `ReplyTimeout` (30s) and one recovery shape,
`Compensate().TransitionTo(Failed).Finalize(TimedOut)`. They differ only in how much they unwind,
because `Compensate()` walks the states the instance actually visited, most-recent first:

| Timed-out state | Unwinds |
| --- | --- |
| `AwaitingInventory` | releases the hold `ReserveInventory` may or may not have taken |
| `AwaitingPayment` | releases the hold and defensively refunds (unchanged behaviour) |
| `AwaitingShipment` | refunds and releases — identical to the `ShipmentFailed` branch |

**`AwaitingInventory` and `AwaitingPayment` were later merged into one `Gathering` state** by the
"Parallel fan-out and join" pass below — this table describes the shape at the time this pass shipped,
and is retained as-is rather than rewritten; the unwind behaviour it describes didn't go away, it moved
onto the merged state's own compensation. The 60 stranded sagas this section already knew about were
backfilled against the pre-merge names (see "Backfilling the 60 stranded sagas" immediately below) —
resolved before the merge could affect them, not because of anything this table needed to change.

**The constraint this places on participants is the interesting part.** A timeout always means "no
reply arrived in time", never "the participant declined" — a decline is a real reply and takes the
explicit `...Failed` branch instead. So compensation here is necessarily *defensive*: the request may
well have succeeded with only its reply lost, which means every compensating message has to be safe to
receive for work that never happened. That is a real design constraint the sample now depends on, not
an incidental detail.

**A live defect this pass found in the choreography shipped one pass earlier.** Checking
`TimeoutScheduled` rows per state against the running stack showed every `PostShipmentChoreography`
milestone with hundreds of scheduled timeouts — except its initial state, which had **zero**. The cause
is an engine rule that fails silently: `SagaOrchestrator` schedules a state's timeout only on a real
transition (`ToState != FromState`), and `OrderShipped` recorded the initial state, so the
instance-creating event was a self-transition that scheduled nothing. An order whose three
post-shipment events were all lost would have hung forever, and the previous README section claimed
that case was covered. Fixed by giving the opening event its own `Shipped` milestone so the transition
is real; `AwaitingFulfilment` is now deliberately excluded from the timeout list, since nothing ever
transitions into an initial state. `InitialStateTimeoutTests` pins both halves of the rule so it can't
regress silently elsewhere.

**Live verification** under the chaos overlay, comparing before and after on the same database:

- `TimeoutScheduled` gained `AwaitingShipment` (0 → 22) and `Shipped` (0 → 15, the choreography fix);
  `AwaitingInventory` went 2 → 46.
- `TimeoutFired` gained `AwaitingInventory` (0 → 8). Before this pass, `AwaitingPayment` was the only
  state that had ever fired a timeout in this database's entire history — 191 of them.
- One `AwaitingInventory` timeout traced end to end through its event log: `ReserveInventory`
  published, reply dropped by chaos, `TimeoutScheduled` → `TimeoutFired` → `CompensationStarted` →
  `ReleaseInventory` published → `CompensationStepSucceeded` → `Failed`/`TimedOut`. InventoryService's
  own logs confirm it received the release, so the compensation reached a real participant rather than
  just being recorded.
- **Not directly observed:** an `AwaitingShipment` timeout firing. Twenty-two were scheduled, but no
  shipment reply happened to be dropped inside the observation window. The code path is the one
  `AwaitingInventory` exercised, and the multi-compensation unwind is the one `AwaitingPayment` has
  fired 197 times, so both halves are covered in combination — but stated plainly rather than implied.

**What this does not do:** it does not rescue the 60 already-stuck sagas. Timeouts are scheduled when a
saga transitions *into* a state, so instances that entered `AwaitingInventory`/`AwaitingShipment` before
this change have no timeout row and never will. They stay stuck. Draining them would need a separate
backfill — deliberately not smuggled into this pass.

## Backfilling the 60 stranded sagas

The separate backfill the section above deferred. `tools/BackfillStrandedTimeouts` is a small one-time
console tool, not a new engine capability or dashboard endpoint — this is a known, bounded fix for two
known states on one known saga type, not a general problem worth new public surface. It reaches directly
into `VSagaDbContext` (ops code against the one concrete provider actually deployed here, not pluggable
engine code, so there's no reason to add new abstraction-interface surface for it) rather than adding a
query method to `ISagaTimeoutStore`, which today only exposes `ScheduleAsync`/`CancelAsync`/`ClaimDueAsync`
— no way to ask "does a pending timeout already exist for this instance", which the tool needs for its
dedup guard.

**The fix reuses the real engine path rather than hand-rolling the unwind.** For every `OrderSaga`
instance `Status == Running` in `AwaitingInventory`/`AwaitingShipment` with no existing pending
`SagaTimeouts` row for that exact state (the dedup guard — an instance that entered either state *after*
the live timeout-coverage fix above already has a normal one, and scheduling a second would be sloppy
even though harmless: `HandleTimeoutAsync`'s own `CurrentState != ForState` guard no-ops the stale one
rather than double-compensating), the tool inserts a `SagaTimeouts` row due immediately. It writes no
compensation or finalization logic of its own — the already-running `SagaTimeoutDispatcherHostedService`
claims the row on its normal 5s poll and runs it through the exact same `SagaOrchestrator.HandleTimeoutAsync`
path a real timeout takes, compensating and finalizing `TimedOut` exactly as the live fix above already
does for new instances.

**Live verification**, under `docker compose up --build` against the real Postgres volume that actually
held the historical stranded rows:

- Pre-run count, confirmed live via direct SQL before touching anything: exactly 60 — 43
  `AwaitingInventory`, 17 `AwaitingShipment`. No drift from the historical number above.
- Ran the tool: scheduled exactly 60 backfill timeouts, all sharing one `DueAtUtc` batch timestamp (a
  useful side effect — the sample's own continuous order load was concurrently creating unrelated fresh
  timeout rows, so a loose "recent `DueAtUtc`" filter over-matched; the shared exact timestamp was the
  reliable way to isolate the backfilled rows afterward).
- ~15s later — one dispatcher poll cycle — all 60 rows had fired and all 60 instances were
  `CurrentState=Failed`, `Status=TimedOut` (43/43, 17/17).
- One instance from each state traced end to end through `SagaEventLog`: both show
  `TimeoutFired → CompensationStarted → MessagePublished(...) → CompensationStepSucceeded →
  StepSucceeded (→ Failed)`, identical in shape to an ordinary timeout — nothing about being backfilled
  is visible on the timeline. The `AwaitingShipment` trace additionally confirms `Compensate()`'s
  most-recent-first order in practice, not just in the DSL: `CompensationStepSucceeded fromState=
  AwaitingPayment` logged before `fromState=AwaitingInventory` — refund, then release, exactly the order
  `ConfigureRecovery` registers them against visited-state history.
- Cross-checked one resolved instance via the live dashboard API (`GET /api/sagas/OrderSaga/{id}`) —
  matches the database exactly.
- **Idempotent.** Re-ran the tool immediately after: found 0 stranded, scheduled 0. Confirmed again on a
  later pass: 0 `OrderSaga` instances stranded in either state, and none have reappeared since.

**Not mutation-tested**, unlike this repo's usual habit for anything envelope/header/linkage-adjacent —
deliberately: the tool contains no new engine logic to mutate. Its only real claim is "insert a row with
these five columns," and the correctness that matters is entirely in `SagaOrchestrator.HandleTimeoutAsync`,
which is already exercised by the engine's own test suite and by every other live timeout in this README.

## Parallel fan-out and join

Closes the last engine item from the original v1 roadmap note: an orchestrated saga can now dispatch
several branches at once and wait for all of them, instead of being limited to one message per state.

**Half of it already worked.** `.Publish(...)` chains, so a single step could always dispatch several
commands — the fan-out needed no new DSL at all. What was missing was the *join*: replies come back in
an order nobody controls, and `TransitionTo(state)` was unconditional, so there was no way to express
"stay here until the last branch reports."

That is the same shape as the choreography join one pass earlier, so it got the same treatment — a
state-dependent overload rather than a new subsystem:

```csharp
During(Gathering)
    .When<StockReserved>()
        .Then((ctx, _) => ctx.Saga.StockReserved = true)
        .TransitionTo(s => s.AllBranchesReady ? ReadyToShip : Gathering)
    .When<PaymentAuthorized>()
        .Then((ctx, _) => ctx.Saga.PaymentAuthorized = true)
        .TransitionTo(s => s.AllBranchesReady ? ReadyToShip : Gathering);
```

Returning the gathering state keeps the saga waiting; returning the next state releases it. Register
the same selector on every branch and whichever reply lands last is the one that advances the saga,
with no branch assuming it is last. `StepDefinition.ResolveTargetState` is the single place the fixed
and computed forms are collapsed, so the orchestrated and choreographed DSLs can't drift — the same
reason `ResolveFinalStatus`, `StepExecutor`, and `CompensationRunner` are shared.

**One timeout covers the whole gather.** Returning the gathering state is a self-transition, which the
orchestrator treats as "no transition" and therefore neither cancels nor reschedules that state's
timeout. That is what a join wants — an arriving branch must not silently extend the deadline — but it
does mean a branch can't carry a separate deadline of its own.

**A correction to an earlier claim in this README.** The choreography pass said `Finalize(selector)`
was deliberately *not* added to the orchestrated `EventBuilder`, on the reasoning that orchestration
gates by state and so can express a conditional ending as separate `During(...)` branches. That holds
in general but not for a *terminal* join, where the last branch to arrive must both release the join
and finish the saga, and no branch knows it is last — a fixed `Finalize(status)` on each branch would
complete the saga on the first reply. `EventBuilder` now has the overload too, and the old note is
corrected in place.

**A silent engine limitation found while writing the tests, now loud.** Giving the second test saga the
same `TState` as the first made it vanish: `AddSaga<TDefinition, TState>` registers the definition as
`ISagaDefinition<TState>`, so a second saga sharing a state class silently wins the registration and
the first never runs — no error, its messages simply go nowhere. `AddSaga` now throws a
`SagaDefinitionException` at registration instead, because the runtime symptom (an inexplicably missing
saga) points nowhere near the cause. Each saga needs its own state class even if two would be
structurally identical.

**Coverage.** `ParallelFanOutJoinTests` pins the fan-out (one step, three outbound commands), the join
across all six arrival orders, the terminal-join variant across all six, that arriving branches don't
reset the gather timeout, and that a stalled gather still times out and compensates.
`SagaRegistrationTests` pins the duplicate-state guard. Verified by mutation rather than assumed:
making `ResolveTargetState` ignore its selector fails 13 tests, all of them in the fan-out suite and
nothing else.

**Now wired into the OrderProcessing sample.** This section originally left `OrderSaga` alone
deliberately — restructuring it is a product decision about what the sample is *for*, not a detail to
fold into the pass that built the primitive, and it doubles as this project's reference for the whole v1
slice, not just an example of the linear shape. Confirmed before touching it rather than defaulted into:
`OrderSaga` now merges its old `AwaitingInventory` and `AwaitingPayment` states into one `Gathering`
state that fans out `ReserveInventory` and `ChargePayment` together and joins on both replies — exactly
the "obvious demonstration" this section named above. The actual blast radius turned out narrow: no
engine or dashboard test is coupled to the sample's real state names (the closest lookalike,
`TestOrderSaga` in `VSaga.Core.Tests`, is a fully independent fixture with its own duplicate contract
types), so this is `samples/VSaga.Samples.OrderProcessing/OrderSaga.cs` plus this section and the
"Timeout coverage for every awaiting state" note above pointing at it.

**A real behavioural change, not just a rename.** Charging the card no longer waits on inventory being
confirmed first — a real trade-off (favours latency over never charging for stock that turns out
unavailable), named here rather than left implicit. And because reserving and charging now run
concurrently, either failure can arrive while the *other* branch has already succeeded — impossible in
the old strictly-sequential shape, where payment was never attempted until inventory had already been
confirmed. Both `InventoryReservationFailed` and `PaymentFailed` therefore compensate now, not just the
payment one as before, and `Gathering`'s own compensation is unconditionally defensive — it always sends
both `ReleaseInventory` and `RefundPayment` rather than checking which branch actually landed, for the
same reason the timeout paths already had to be defensive.

**A real concurrency bug this pass found, not merely a hypothetical one.** The first version of
`Gathering`'s compensation sent its two messages via `Task.WhenAll`, on the reasoning that two
independent publishes should be able to run concurrently. Live chaos testing disagreed within minutes:
`ctx.PublishAsync` shares this saga's own context (and, transitively, the one EF Core `DbContext` behind
its event log) across every action in a step, and that is only ever safe one operation at a time — the
same reason every `.Publish(...).Publish(...)` chain elsewhere in this DSL runs sequentially. Concurrent
publishes threw `InvalidOperationException: A second operation was started on this context instance
before a previous operation completed` 22 times in one run, and — the actually damaging part — 13 of 20
compensation attempts in that window logged `CompensationStepFailed` rather than
`CompensationStepSucceeded`, meaning a real order's release/refund could silently only half-send. Fixed
by awaiting the two publishes sequentially instead; the re-run below shows zero exceptions and zero
compensation failures under the same chaos settings. Nothing about the `.Publish(...)` chains already
used everywhere else in this DSL was ever at risk — those were already sequential by construction — this
was specific to writing a multi-publish compensation delegate by hand with the wrong assumption about
concurrency safety.

**Live verification**, under `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d
--build` against the real stack, counts filtered to instances created after the `order-processing`
container's own restart (the Postgres volume is reused, not reset):

- First run (with the `Task.WhenAll` bug still in place), 32 fresh instances: 11 `Completed`, 8
  `Failed`/`Failed` (an explicit `...Failed` reply), 10 `Failed`/`TimedOut`, 3 still `Gathering`. 22
  `DbContext` concurrency exceptions logged, 13 of 20 compensation attempts recorded
  `CompensationStepFailed`.
- One instance from that run traced end to end shows exactly the new race this restructuring makes
  possible: `PaymentCharged` landed first (a real charge), then — about a second later —
  `InventoryReservationFailed` arrived (chaos-simulated out-of-stock). Compensation correctly fired both
  `ReleaseInventory` and `RefundPayment` — refunding a charge that had genuinely already gone through,
  a scenario the old sequential design could never reach from this branch, since payment was never
  attempted until inventory had already succeeded.
- A second traced instance shows the timeout half of the same story: `PaymentCharged` landed, but no
  inventory reply ever arrived (chaos-dropped); `Gathering`'s own 30s timeout fired
  (`TimeoutFired` ~31.5s after entering `Gathering`) and compensation again sent both messages
  defensively, correctly refunding the real charge alongside a release for a reservation that may or may
  not have happened.
- A traced happy-path instance confirms the join itself: `PaymentCharged` arrived first and only
  self-transitioned (no `ShipOrder` published yet, since `InventoryReserved` was still false);
  `InventoryReserved` arrived second, both flags true, `ShipOrder` published from *that* branch's own
  `Then`, and the saga proceeded to `AwaitingShipment` → `Completed` on `OrderShipped` exactly as before.
- After the `Task.WhenAll` fix, a fresh run (same chaos settings, new container start) of 32 fresh
  instances: 13 `Completed`, 7 `Failed`/`Failed`, 11 `Failed`/`TimedOut`, 1 still `Gathering`. **Zero**
  `DbContext` concurrency exceptions and **zero** `CompensationStepFailed` entries across the whole
  window — confirming the fix, not just assuming it from reading the diff.
- The mirror of the traced race above turned up in the same post-fix run: `PaymentFailed` arrived first
  (card declined, nothing charged), compensation correctly fired, and the saga finalized `Failed` — then,
  about a second later, a slow `InventoryReserved` reply landed against the now-terminal instance.
  Logged as `UnexpectedEvent` against `FromState=Failed`, exactly the existing "late reply after
  completion" handling this engine already has elsewhere, not a new failure mode this restructuring
  introduced — worth citing as confirmation, not as a gap.
- Zero unhandled exceptions or crash-level log lines from either container across either run, aside from
  the concurrency exception itself (caught internally by `HandleStepFailureAsync`'s own catch, not a
  crash — it just meant the compensation step recorded as failed rather than succeeded).

**Not unit-tested at the sample level**, matching this repo's established pattern for every other sample
saga (`InvoiceDeliverySaga`, `InvoiceFollowUpSaga`, `InvoiceArchivalSaga`): the underlying join primitive
is already tested by `ParallelFanOutJoinTests`/`TestParallelFulfilmentSaga`, so `OrderSaga`'s own
correctness is verified live rather than by a redundant unit-test double.

## Sub-saga composition: parent linkage

The first of the three slices in [`docs/sub-saga-composition.md`](docs/sub-saga-composition.md). A saga
can start another saga as a step, and the child records which instance started it:

```csharp
.Then((ctx, m) => ctx.StartChildAsync(new DeliverInvoice(m.OrderId, m.InvoiceNumber), ctx.CancellationToken))
```

`StartChildAsync` publishes that message under a **fresh** correlation id with two new envelope headers
(`x-vsaga-parent-saga-type`, `x-vsaga-parent-correlation-id`). Whichever saga's `CanInitiate` matches
becomes the child, and `SagaOrchestrator` reads those headers exactly once — when it creates the
instance — onto `SagaState.ParentSagaType`/`ParentCorrelationId`. Neither saga type references the
other; the whole link is two strings on the wire.

**A fresh id, not the parent's.** Sharing would have been simpler, and is wrong: the snapshot primary
key is `(SagaType, CorrelationId)`, so a shared id caps a parent at one child *per saga type*, and a
self-recursive saga collides with itself outright. This is also what distinguishes the relation from
the one in "Saga identity: (SagaType, CorrelationId)" above — `PostShipmentChoreography` shares
`OrderSaga`'s id because they are one transaction observed twice, whereas a child is a separate unit of
work under its own id. The dashboard shows them as two different strips, and
`/api/correlations/{id}` deliberately does not return children.

**Real columns, not just the state blob.** `ParentSagaType`/`ParentCorrelationId` ride along inside
`DataJson` for free, but `ISagaSummaryReader` is saga-type-agnostic and queries columns, so answering
"which sagas did this one start?" needs them projected out. Hence the `AddSagaParentLinkage`
migration (two nullable columns plus an index on the pair), `SagaSummary` carrying the pointer, and
`FindChildrenAsync` behind `GET /api/sagas/{sagaType}/{correlationId}/children`. Root sagas leave both
null, so on a workload with no sub-sagas that index stays effectively empty.

**What this deliberately does not do: waiting.** `StartChildAsync` returns as soon as the publish does.
The design sketch expected the parent's wait to need no new engine work — park in a state, let the
child's own message release it via the existing join primitive — and the *parent* half of that is
indeed already there. The **child** half is not, and this is the one claim in that document that
building it disproved: a child cannot address its parent at all today. `ctx.PublishAsync` always
stamps the publishing saga's own correlation id, and the orchestrator correlates strictly on the
inbound correlation id, so a child publishing "I'm done" sends it under the child's id, where the
parent will never see it. `CorrelateBy` does not rescue this — it is a business key for dashboard
search, explicitly not used for routing. So "child publishes its own domain message", recorded there as
working today with no engine change, actually needs one: a publish overload that takes a target
correlation id. That changes the trade-off against an engine-published `ChildSagaFinished`, and the
decision is still open in the doc rather than quietly settled here.

**Compensation does not cascade into children — a closed decision, not a gap.** A parent's
`.Compensate()` only ever runs the parent's own registered compensation delegates; it never walks into
`FindChildrenAsync` or touches a child automatically. Considered and closed as "analysed, deliberately
not built" in [`docs/sub-saga-composition.md`](docs/sub-saga-composition.md) §3.5 (Slice 3): the parent
has no compile-time link to its children (`StartChildAsync` returns `Task`, not a child id), the child
tree is unbounded in depth since a child gets a fresh correlation id specifically so a saga can start
its own type, and — checked directly against this code before closing — compensation delegates run
against `ISagaContext<TState>`, which has no children-lookup method at all; only
`ISagaSummaryReader.FindChildrenAsync` does, and that is a read-model query compensation logic has no
route to. A parent that needs a child compensated publishes its own compensating command explicitly,
the same way it would address any other collaborator.

(A started child *is* distinguishable on the parent's own timeline, via the dedicated `ChildSagaStarted`
entry type — see "Sub-saga composition: engine safety net" below for that and its `ChildSagaFinished`
counterpart.)

**Failure modes, documented rather than discovered.** `StartChildAsync` is a publish, so if no saga
initiates on that message type, no child is created and nobody is told — the parent transitions and
parks exactly as it would have on success, and only its own timeout eventually notices.
`AChildMessageNobodyInitiatesOn_StartsNothingAndTellsNobody` pins that as tested behaviour rather than
a surprise. Symmetrically, two saga types initiating on one child message would start two children with
the parent none the wiser. A half-stamped link (one header present, the other missing or unparseable)
is recorded as a root rather than a child, since `FindChildrenAsync` matches on the pair and a
one-sided link would show in the dashboard while being unreachable from the parent.

**In the sample.** `PostShipmentChoreography` now starts an `InvoiceDeliverySaga` per `InvoiceIssued`.
Issuing an invoice is not the same as the customer receiving it, and getting it delivered has its own
retry window (20s, deliberately different from `OrderSaga`'s 30s — not inheriting the parent's
deadlines is half the reason to make it a separate saga) and its own failure ending. `OrderSaga` is
untouched: this pass adds no state, timeout, or compensation to it, and the question of restructuring
it to demonstrate composition is left open in the design doc, exactly as the parallel fan-out pass left
it. `NotificationParticipant` grew a `SendInvoiceEmail` handler and neither knows nor cares that the
correlation id it is replying to belongs to a child saga.

**Live verification**, since the linkage rides on envelope headers and this repo has previously shipped
header threading the orchestrator never read (`SourceService`/`CausationId`, whose tests hand-built the
field and so proved nothing). Under `docker compose up` against real Postgres and RabbitMQ:

- The redeploy is a clean cutover in the data, which is the strongest available evidence that the
  linkage came from this code and not from somewhere else: of the `PostShipmentChoreography` instances
  that reached `InvoiceIssued`, every one created before the restart has zero children and **every one
  created after has exactly one** — 39/39, none with zero, none with two.
- 43 child instances, all with both columns populated. Zero half-linked rows
  (`ParentSagaType IS NULL <> ParentCorrelationId IS NULL`), and zero dangling pointers — every child's
  parent pair resolves to a real row.
- All three of the child's endings actually fired: `Delivered`/`Completed` 34,
  `Undeliverable`/`Failed` 8 (simulated bounce), `Undeliverable`/`TimedOut` 1 — the last under the
  chaos overlay, which dropped a `SendInvoiceEmail` and left that child to its own 20s timeout.
- One completed child traced end to end through its own timeline: `SagaStarted DeliverInvoice` →
  `MessagePublished SendInvoiceEmail` → `TimeoutScheduled AwaitingDelivery` → `MessageReceived
  InvoiceEmailSent` → `Delivered`/`SagaCompleted`. The timed-out one shows the same opening, then
  `TimeoutFired` → `Undeliverable`. Both entirely under the child's own correlation id.
- **A failed child does not touch its parent**, which is the property "does not wait" is supposed to
  buy: across all 43 pairs, every parent is `Completed` regardless of whether its child completed,
  bounced, or timed out.
- `curl .../children` returns the child with its parent pointer, and `curl` on the child returns a
  summary pointing back — while `/api/correlations/{parentId}` returns only `OrderSaga` and
  `PostShipmentChoreography`, correctly excluding the child.
- **Not verified in a browser:** the "started by" / "started" strips are covered by component tests
  and by the endpoint they read from, but the rendered page was not opened during this pass.

**Verified by mutation, twice**, once per end of the wire. Making `SagaOrchestrator` read a header name
nothing stamps fails 5 tests, all in `SubSagaCompositionTests` and nothing else; making `SagaContext`
publish the child's message without the linkage headers fails the same 5. The interesting half is what
*doesn't* fail: the EF Core provider tests and the dashboard endpoint tests stay green under both
mutations, because they set the parent pointer by hand. That is correct for them — their subject is the
store and the route — but it is also an exact reproduction, under lab conditions, of how the
`CausationId` tests passed for months against a header nobody read. The tests that can actually catch
it are the ones that never touch the linkage themselves and let `StartChildAsync` → transport →
orchestrator set it.

## Sub-saga composition: completion notification

Slice 2a of [`docs/sub-saga-composition.md`](docs/sub-saga-composition.md). A child can now address its
parent directly, and a parent can actually wait for the answer rather than only parking until its own
timeout:

```csharp
.Then((ctx, m) => ctx.NotifyParentAsync(new InvoiceArchivalFinished(ctx.Saga.OrderId!, Archived: true), ctx.CancellationToken))
```

`NotifyParentAsync` publishes under `Saga.ParentCorrelationId` — the same field `StartChildAsync`
stamped onto this instance when the engine created it — so a parent parked in a `During(state)` waiting
for a specific message type sees it arrive as an ordinary inbound message. Not a general
publish-under-any-id overload: the only id this can address is the one the engine already put on this
saga's own state, so nothing can mint an orphan instance under an id it invented. It throws immediately,
before any I/O, if this saga has no parent.

**Why this needed an engine change at all**, corrected from the original design sketch: `PublishAsync`
always stamps the *publishing* saga's own correlation id, and the orchestrator correlates strictly on
the inbound id, so a child's `PublishAsync("I'm done")` was never reaching its parent — it addressed the
child's own instance. `CorrelateBy` doesn't rescue this either; it's a business key for dashboard search,
not routing. `NotifyParentAsync` is the missing piece: the only new capability is publishing under a
correlation id this saga did not itself open.

**Two options were on the table** (`docs/sub-saga-composition.md` §3.4) — a child publishing its own
result, or an engine-published `ChildSagaFinished(status)` — and working through them turned up that
they're complementary, not alternatives: only a child's own message can carry its actual result (what
was charged, whether the invoice archived), and only an engine-published event could ever fire when a
child fails via an unhandled exception or simply times out, since neither reaches the child's own
publish step. This pass builds the first half. The second — an opt-in safety net for the cases this
one structurally cannot reach — remains proposed as Slice 2b.

**In the sample: a new pair, not a retrofit onto `PostShipmentChoreography`.** `InvoiceFollowUpSaga`
starts an `InvoiceArchivalSaga` per `InvoiceIssued` and waits for it — the first live parent in this
repo that parks and is released early rather than only by timeout. It is deliberately *not*
`PostShipmentChoreography` gaining a wait: that saga's own doc comment is explicit that its leg must
complete once all three post-shipment services have reported, and an undeliverable invoice must not
hold it open. Waiting needs a state that parks (`During(state).When<T>().TransitionTo(...)`), which is
the orchestrated DSL's shape, not the choreography's `On<T>().Finalize(Func)` join — retrofitting a park
onto it would have meant either blocking its completion on archival, contradicting its own documented
invariant, or reacting to a message after the instance is already terminal, neither of which
demonstrates a real wait. It's also a new *child*, `InvoiceArchivalSaga`, rather than a second observer
of `InvoiceDeliverySaga`: reusing that one would file two customer emails per invoice instead of one
email and one accounting copy — a different, real concern, with its own real failure mode (the archive
store being unavailable). `InvoiceFollowUpSaga` itself shares the order's correlation id, the same as
`PostShipmentChoreography` — both react to `InvoiceIssued`, so both simply open under whatever
correlation id that message already carries, no special wiring required.

**The child's own timeout is deliberately not covered.** `InvoiceArchivalSaga` calls
`NotifyParentAsync` from both terminal steps that a real reply reaches — `InvoiceCopyStored` and
`InvoiceCopyStorageFailed` — but not from its own `WithTimeout`. A timed-out child never reaches any
step that could call it, which is exactly the structural gap Slice 2b exists to cover; leaving it
uncovered here is what makes `InvoiceFollowUpSaga`'s own, longer timeout (30s, against the child's 15s)
do real work rather than being redundant. Verified live below.

**A race this pass found, not one it went looking for.** A child that calls `NotifyParentAsync` from
the very same step that `StartChildAsync` started it in can race ahead of the parent's own
not-yet-persisted transition. `InMemoryMessageTransport.DispatchAsync` invokes every subscriber
synchronously and recursively, so such a notification is still nested inside the parent's own
`StartChildAsync` call when it arrives — before the parent has persisted its state, or, for a brand-new
parent, inserted a row at all. `SagaOrchestrator.HandleCoreAsync` finds no existing instance, decides
the message isn't among the parent's initiating types, and logs `UnexpectedEvent` — no exception, so no
redelivery either, and the notification is silently gone. Pinned by
`NotifyParentAsync_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`
(`tests/VSaga.Core.Tests/NotifyParentAsyncTests.cs`) rather than quietly avoided. Not fixed: a real fix
would mean reordering this engine's "run step actions, then persist" sequence throughout, well beyond
this pass. Real transports decouple a child's dispatch from the publisher's call stack rather than
nesting it, and every real child in this repo has genuine I/O between the two calls (a participant
round-trip), so this did not reproduce once under 21 real archival children live — but a child that
reports back with no intervening work at all remains a real, narrow hazard, worth knowing rather than
assuming away.

**Live verification**, under `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d`
(chaos on, so the child's own timeout actually has a chance to fire — see the "Chaos-engineering
transport middleware" section):

- 21 `InvoiceArchivalSaga` children, 23 `InvoiceFollowUpSaga` parents, zero dangling parent pointers and
  zero half-linked rows — the same shape of check Slice 1's linkage got, now over a notification path
  running the opposite direction.
- All three endings fired: 19 `Archived`/`Completed`, 1 `Failed`/`Failed` (a real archive-store
  failure), 1 `Failed`/`TimedOut`.
- The timed-out pair traced end to end: the child's own timeline shows `TimeoutFired` →
  `StepSucceeded AwaitingStorage → Failed`, with no `NotifyParentAsync` publish anywhere on it. Its
  parent reached `Abandoned`/`TimedOut` roughly 35s after creation — its own 30s timeout, not the
  child's 15s one, and not a notification — which is live confirmation that the child's timeout does
  not notify and the parent's independent timeout is genuinely what rescues it.
- A completed pair traced end to end: `SagaStarted InvoiceIssued` → `MessagePublished ArchiveInvoice` →
  `TimeoutScheduled` → `MessageReceived InvoiceArchivalFinished` → `StepSucceeded AwaitingArchival →
  Archived` → `SagaCompleted`, entirely on the parent's own timeline.
- **The fan-out note from §3.4 turned out narrower than written there.** `InvoiceFollowUpSaga` shares
  its correlation id with `OrderSaga` and `PostShipmentChoreography`, so in principle
  `InvoiceArchivalFinished` "fans out to every saga type tracking that id." In practice, subscription is
  per explicitly-declared message type, not per correlation id — confirmed by pulling `OrderSaga`'s own
  timeline for an order that went through this path: no `InvoiceArchivalFinished` entry, because
  `OrderSaga` never declares a handler for it and so never even subscribes.

**Verified by mutation, twice**, same discipline as Slice 1. Publishing under the child's own
correlation id instead of `Saga.ParentCorrelationId` fails exactly the 3 tests in
`NotifyParentAsyncTests` that depend on a real notification reaching its parent, and nothing else.
Treating the read of `Saga.ParentCorrelationId` as always absent fails the same 3.

## Sub-saga composition: engine safety net

Slice 2b of [`docs/sub-saga-composition.md`](docs/sub-saga-composition.md), and the last of the three
slices that section originally scoped. `ctx.NotifyParentAsync` (Slice 2a) only works when a child's own
step code reaches a point where it can call it — two cases structurally cannot reach that point at all:
a child that fails via an unhandled exception, and a child that times out. The engine now covers both by
publishing `ChildSagaFinished` to the parent on the child's behalf:

```csharp
public sealed record ChildSagaFinished(Guid ChildCorrelationId, string ChildSagaType, SagaStatus Status);
```

Published directly by `SagaOrchestrator` — not through `ISagaContext`, unlike every other outbound
message in this engine — because this is the engine speaking on a child's behalf, not the child's own
step code. It fires from exactly two places: `HandleStepFailureAsync`'s exception path (a child's step
threw, so `HandleAsync` never returned a normal outcome) and `HandleTimeoutAsync`'s timeout path, but
only when the timeout goes terminal. It deliberately does **not** fire from the ordinary
message-driven success path, even when that path finalizes the saga — that's `NotifyParentAsync`'s
territory, and a child that reports its own result there needs no redundant, data-free duplicate from
the engine.

**Opt-in fell out of a mechanism that already existed, with no new DSL call.** The design sketch
considered a new per-definition call mirroring `OnUnhandledEvent(policy)`. Building it showed that was
unnecessary: `SagaRuntime<TState>.Subscription` is built from `ISagaDefinition.MessageTypes` — the union
of every message type a saga has declared a handler for, in any state. A parent that never declares
`.When<ChildSagaFinished>()` anywhere in its own DSL is therefore never even subscribed to the message
type, so the transport never delivers it — the same reason `InvoiceArchivalFinished` never reaches
`OrderSaga`/`PostShipmentChoreography` despite them sharing a correlation id with `InvoiceFollowUpSaga`
(see the completion-notification section above). Declaring the handler *is* the opt-in.

**A documentation correction, found while checking why opt-in mattered at all.** The design doc claimed
`UnhandledEventPolicy.Throw` makes an unhandled message "nack and redeliver forever." Reading
`SagaOrchestrator.RunStepAsync` shows that's not what happens: the exception an unhandled event throws
under `Throw` is caught by `RunStepAsync`'s own catch block and routed to `HandleStepFailureAsync` — the
same path a genuine step failure takes — which marks the saga `Failed` and **acks** the message. There is
no redelivery loop. The real hazard `Throw` poses to an un-opted-in parent is a silent, one-shot false
`Failed`, not an infinite spin — corrected in `docs/sub-saga-composition.md`. Either way, the conclusion
that (b) needs to be opt-in per parent stood; only the mechanism and the specific failure mode changed.

**A race, analogous to Slice 2a's, found the same way.** A child that fails via exception in the very
same step `StartChildAsync` started it in publishes `ChildSagaFinished` while still nested inside the
parent's own `StartChildAsync` call, under `InMemoryMessageTransport`'s synchronous/recursive dispatch —
before the parent has persisted its own transition. Same outcome as the `NotifyParentAsync` race:
`UnexpectedEvent`, silently dropped, no redelivery. Only the StepFailed path can race this way — a
timeout is dispatched independently by `SagaTimeoutDispatcherHostedService`'s own poll loop and can never
nest inside a `StartChildAsync` call. Pinned by
`ChildSagaFinishedTests.ChildSagaFinished_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`,
not fixed, for the same reason Slice 2a's race wasn't: a fix means reordering this engine's "run step
actions, then persist" sequence throughout, well beyond this slice's scope.

**A dedicated timeline entry for the whole child-linkage story, not just the finish.** `SagaEntryType`
gained `ChildSagaStarted` (retagging `StartChildAsync`'s own publish, previously an ordinary
`MessagePublished` per Slice 1's deferred note) alongside `ChildSagaFinished`, both appended after
`MessageSent` per the append-only rule. Both are still, mechanically, outbound publishes, so
`SagaMapBuilder` treats them exactly like `MessagePublished`/`MessageSent` for edge-stitching — no
bespoke map logic needed. The Angular timeline/map views render `entryType` as plain text with no
per-type icon switch, so the frontend change was two string literals in `saga.model.ts`.

**In the sample.** `InvoiceFollowUpSaga` opts in with a `.When<ChildSagaFinished>()` branch on
`AwaitingArchival`, alongside its existing `.When<InvoiceArchivalFinished>()` branch:

```csharp
.When<ChildSagaFinished>()
    .Then((ctx, _) => ctx.Saga.InvoiceArchived = false)
    .TransitionTo(Abandoned)
    .Finalize(SagaStatus.TimedOut);
```

`InvoiceArchivalSaga`'s own timeout branch is unchanged — it still never calls `NotifyParentAsync`, by
design (see its doc comment). Before this slice, that meant `InvoiceFollowUpSaga` could only learn about
a timed-out child via its own independent 30s timeout. Now the engine's safety net reaches it in ~15s,
as soon as `InvoiceArchivalSaga`'s own `StorageTimeout` fires — the parent's 30s timeout becomes a true
backstop rather than the only rescue.

**Live verification**, under `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d
--build` (chaos on, so `InvoiceArchivalSaga`'s `StoreInvoiceCopy` timeout actually has a chance to fire):

- A ~8-minute run against a freshly-built stack (excluding sagas from the volume's pre-existing history,
  which predate this pass's code — see below) produced 20 `InvoiceFollowUpSaga` instances: 12
  `Archived`/`Completed`, 7 `Abandoned`/`TimedOut`, 1 still `Running`.
- **Of the 7 timed-out parents, the split is exactly what the design predicts**: 4 were rescued by the
  engine safety net in 17.4–23.3s (`MessageReceived ChildSagaFinished` on the parent's own timeline, no
  `TimeoutFired` at all), and 3 hit the parent's own 30s backstop timeout at 31.4–34.1s (`TimeoutFired`,
  no `ChildSagaFinished` ever arrived) — the true-backstop behaviour the design intends when chaos also
  drops the safety-net publish itself, or the notification simply loses the race to nothing.
- **One pair traced end to end.** Child `InvoiceArchivalSaga` (created 17:11:02.347): `SagaStarted
  ArchiveInvoice` → `MessagePublished StoreInvoiceCopy` → `TimeoutScheduled AwaitingStorage` →
  `TimeoutFired` (15s later) → `StepSucceeded AwaitingStorage → Failed` → **`ChildSagaFinished`** — the
  new dedicated entry type, not `MessagePublished`. Its parent `InvoiceFollowUpSaga` shows
  `MessageReceived ChildSagaFinished` 8ms after that, `StepSucceeded AwaitingArchival → Abandoned`,
  `SagaCompleted` — 17.4s total from the parent's own `SagaStarted`, well under its 30s
  `ArchivalWaitTimeout`.
- **The scope boundary held live, not just in tests.** `InvoiceArchivalSaga` also produced 2 fresh
  `Failed`/`Failed` instances via its ordinary business-failure path (`InvoiceCopyStorageFailed` →
  `NotifyParentAsync` → `Finalize(Failed)`) — traced end to end, neither one logged a `ChildSagaFinished`
  entry alongside its `MessagePublished InvoiceArchivalFinished`, confirming the engine safety net stays
  silent on the ordinary success path exactly as designed.
- **Linkage integrity, same check as Slices 1 and 2a**: 26 fresh `InvoiceArchivalSaga` children, zero
  half-linked rows, zero without a parent.
- **The fan-out stayed narrow, confirmed live**: pulling `OrderSaga`'s and `PostShipmentChoreography`'s
  own timelines for a rescued order (all three share the correlation id) shows neither ever received
  `ChildSagaFinished` — neither declares a handler for it, so neither is even subscribed.
- Zero unhandled exceptions or crash-level log lines in either container across the whole run.
- **Not observed live**: the `HandleStepFailureAsync`/unhandled-exception path. Nothing in the
  `OrderProcessing` sample throws an unhandled exception from a step, so only the timeout path actually
  exercised the engine safety net under real chaos timing — an honest gap, covered instead by
  `ChildSagaFinishedTests`' real publish→receive→orchestrator path and the mutation pass above, the same
  way this repo has treated every other live-verification gap.
- **Incidental finding, not a defect**: the compose run reused an existing Postgres volume rather than a
  fresh one. One pre-existing `InvoiceFollowUpSaga` instance (created ~40 minutes before this run's
  containers started) shows its child-start hop as the old `MessagePublished` rather than
  `ChildSagaStarted`, because it was created by the previous image before this pass existed — a live,
  incidental demonstration of "additive, upgrades in place" working as intended, the same property the
  migrations elsewhere in this README rely on. Excluded from the counts above, which only cover instances
  created after this run's containers started.

**Verified by mutation, four ways**, same discipline as Slices 1 and 2a. Publishing under the child's
own correlation id instead of the parent's fails exactly the 4 tests that depend on real delivery to the
parent, and nothing else. Removing the root-saga guard (falling back to the child's own id instead of
skipping) fails exactly the one test that pins a root saga never publishing. Making the ordinary
message-driven success path also publish `ChildSagaFinished` fails exactly the one scope-boundary test —
confirmed clean across the rest of the solution's test suite (all 201 tests across every project), not
just this file, since no other registered parent in this repo both declares a `ChildSagaFinished`
handler and has a child that finishes ordinarily. Dropping `StartChildAsync`'s `ChildSagaStarted`
entry-type override back to the old `MessagePublished` fails exactly the one Slice-1-era test updated
for this slice.

## Transport adapter: Wolverine

`IMessageTransport`'s own doc comment (`src/VSaga.Abstractions/Transport/IMessageTransport.cs:4-6`) names
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
the doc comment forbids. `RawEnvelope` (`src/VSaga.Transport.Wolverine/RawEnvelope.cs`) is the fix: every
single piece of vSaga traffic — regardless of its real message type — is sent and received as this one
empty marker type, so Wolverine's handler discovery only ever has to know about one static
`RawEnvelopeHandler.Handle` method (`RawEnvelope.cs`), never a saga-specific type. The four vSaga headers,
the real message type name, the correlation id, and the message id all travel inside a small
self-describing JSON payload (`WireEnvelope`, `src/VSaga.Transport.Wolverine/WireEnvelope.cs`) carried
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
before returning (see `SagaOrchestrator.HandleAsync`, `src/VSaga.Core/Runtime/SagaOrchestrator.cs:39-50`).
`WolverineAckContext` (`src/VSaga.Transport.Wolverine/WolverineAckContext.cs`) bridges the two: by the
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
(`tests/VSaga.Transport.Wolverine.Tests/WolverineTransportTests.cs`) asserts that actual, verified
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

## Transport adapter: MassTransit

`VSaga.Transport.MassTransit` (`src/VSaga.Transport.MassTransit/`) is the second real
`IMessageTransport` adapter, alongside `VSaga.Transport.RabbitMQ`. Same contract
(`src/VSaga.Abstractions/Transport/IMessageTransport.cs:8-32`), same four methods, same
`MiddlewarePipelineTransport` wrapper — a different wire underneath. Pinned to **MassTransit
8.5.8** (`src/VSaga.Transport.MassTransit/VSaga.Transport.MassTransit.csproj`), the latest 8.x
release confirmed on NuGet at the time of writing: MassTransit v9 is transitioning to a commercial
license, v8 remains Apache-2.0, and this adapter is built on it deliberately rather than on
whatever happened to be cached in training data.

**Built on MassTransit's transport, not its saga features — same boundary the doc comment already
states.** `IMessageTransport`'s own doc comment says concrete adapters "never use another bus's
native saga/state-machine features, only its transport"
(`src/VSaga.Abstractions/Transport/IMessageTransport.cs:4-6`). `MassTransitTransport` is built
entirely on `IBus`/`IPublishEndpoint`/`ISendEndpointProvider` for outbound and
`IConsumer<T>`/`ConsumeContext<T>` for inbound, over MassTransit's RabbitMQ transport — never
Courier (routing slips) and never Automatonymous/its own saga persistence. `SagaOrchestrator`
still owns every bit of retry, redelivery, and dedup (`src/VSaga.Core/Runtime/SagaOrchestrator.cs:52-90`);
this adapter only moves bytes.

**One MassTransit contract for every vSaga message, not one per type.** MassTransit's own pub/sub
topology is built around compile-time generics (`Publish<T>`, `IConsumer<T>`), but
`TransportSubscription.MessageTypes` only ever hands `SubscribeAsync` a list of runtime `Type`
instances — the same mismatch `RabbitMqTransport` sidesteps by treating the RabbitMQ.Client body as
opaque JSON bytes. `VSagaEnvelopeMessage` (`src/VSaga.Transport.MassTransit/VSagaEnvelopeMessage.cs`)
is the one fixed record every vSaga message actually travels as: `MessageTypeName` plus an
already-`System.Text.Json`-serialized body. `AddVSagaMassTransit`
(`src/VSaga.Transport.MassTransit/ServiceCollectionExtensions.cs`) forces it onto one durable
topic exchange (`cfg.Message<VSagaEnvelopeMessage>(m => m.SetEntityName(...))`,
`cfg.Publish<VSagaEnvelopeMessage>(p => p.ExchangeType = "topic")`) and reads the routing key back
off the message itself (`cfg.Send<VSagaEnvelopeMessage>(s => s.UseRoutingKeyFormatter(ctx =>
ctx.Message.MessageTypeName))`) — the same shared-topic-exchange-plus-per-type-routing-key shape
`RabbitMqTransport` gets natively, reconstructed one layer up. `SubscribeAsync`
(`src/VSaga.Transport.MassTransit/MassTransitTransport.cs`) turns off MassTransit's default
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
nothing (see "Sub-saga composition: parent linkage" above).

**Ack/nack, adapted rather than replicated.** MassTransit has no mid-flight equivalent of
RabbitMQ.Client's channel-level `BasicAck` — a consumer settles a delivery only by returning
normally (ack) or throwing (fault). `VSagaEnvelopeConsumer` bridges this onto
`IMessageAckContext`: `AckAsync`/`NackAsync` just record the caller's decision, and `Consume`
turns a recorded nack — or no decision at all — into a thrown exception once the handler
completes. Every receive endpoint is configured `UseMessageRetry(r => r.None())`, so a fault lands
straight in `{queue}_error` rather than being retried by MassTransit itself against
`SagaOrchestrator`'s own bounded-redelivery wishes (`HandleInfrastructureFailureAsync`,
`src/VSaga.Core/Runtime/SagaOrchestrator.cs:61-90`, never relies on broker-native requeue). No
poison-queue/DLX topology is replicated from `RabbitMqTransport` — the scope note calling that
defense-in-depth specific to that adapter, not a contract requirement, held up under actual
implementation.

**Unroutable publish.** MassTransit surfaces RabbitMQ's mandatory-publish-plus-return semantics as
`MessageReturnedException` (confirmed against MassTransit's own test suite,
`tests/MassTransit.RabbitMqTransport.Tests/Mandatory_Specs.cs`) when `PublishContext.Mandatory` is
set and no queue is bound for the routing key. `MassTransitTransport` sets it on every publish and
wraps the exception into the same provider-agnostic `MessageTransportPublishException` RabbitMQ's
own adapter throws, `IsUnroutable` included.

**Tests: 4 against a real broker, no mocks** (`tests/VSaga.Transport.MassTransit.Tests/`,
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

## Transport adapter: Brighter

`VSaga.Transport.Brighter` (`Paramore.Brighter` + `Paramore.Brighter.MessagingGateway.RMQ.Async`
10.7.0, latest stable at the time of writing) implements `IMessageTransport` directly on Brighter's own
transport-level primitives — `RmqMessageProducer`'s `IAmAMessageProducerAsync.SendAsync` to publish, and
`RmqMessageConsumer`'s `IAmAMessageConsumerAsync` to receive/ack/reject — never Brighter's
`CommandProcessor` dispatch pipeline, its Outbox/Inbox, its request-handler routing, or any
workflow/scheduler feature. Same rule this repo already applies to RabbitMQ.Client directly
(`src/VSaga.Abstractions/Transport/IMessageTransport.cs:4-6`): vSaga never uses another bus's own
saga/state-machine machinery, only its wire-level publish/consume primitives.
`src/VSaga.Transport.Brighter/BrighterTransport.cs:78-146` (publish) and `:148-232` (subscribe/consume)
are the whole adapter; `ServiceCollectionExtensions.AddVSagaBrighter` wraps it in the same
`MiddlewarePipelineTransport` every other adapter shares, so chaos/topology-recording middleware work
unchanged (`src/VSaga.Transport.Brighter/ServiceCollectionExtensions.cs`).

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
  (`src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs:70-74`); that path doesn't exist here. Instead,
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
`src/VSaga.Abstractions/Transport/IMessageTransport.cs:26-30`). `SubscribeAsync` forces that eagerly with
a 50ms warm-up receive before starting the consume loop (`BrighterTransport.cs:173-178`). Skipping it
reproduces the exact bug the sub-saga headers are supposed to prove don't exist: a header nobody actually
reads because the message carrying it was silently dropped before a real receive path ever touched it.

**Known gap: no unroutable-publish detection.** `RabbitMqTransport` publishes with `mandatory: true`
plus RabbitMQ.Client's native publisher-confirm tracking, so an unroutable message throws
`MessageTransportPublishException` deterministically
(`src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs:79-88`). `Paramore.Brighter.MessagingGateway.RMQ.Async`
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
`tests/VSaga.Transport.Brighter.Tests/BrighterTransportTests.cs`'s
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
`received.Headers` — `src/VSaga.Core/Runtime/SagaOrchestrator.cs:70-74`) would carry Brighter-internal
noise as bogus outbound headers on every redelivered message. Every real vSaga header in this codebase
is `x-vsaga-`-prefixed with no exception, so the filter drops nothing that matters.

**Mutation-tested the same way the RabbitMQ adapter's own header handling gets no free pass on.**
Deliberately removing the `envelope.Headers` copy loop in `BuildOutboundMessage`
(`BrighterTransport.cs:98-103` at the time) reran all four tests: exactly
`PublishAndSubscribe_PropagatesAllFourVSagaHeadersUnchanged` failed
(`KeyNotFoundException: The given key 'x-vsaga-source-service' was not present in the dictionary`), the
other three stayed green. Reverting the break brought all four back to green. The three that don't set
custom headers genuinely can't catch a header-dropping bug — same lesson this repo already drew from the
`CausationId` header story in "Sub-saga composition: parent linkage" above.

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
(`src/VSaga.Transport.RabbitMQ/RabbitMqConnectionManager.cs:26-27`), so this class of issue isn't unique
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

## Getting started

**Build and test:**

```bash
dotnet build VSaga.slnx
dotnet test VSaga.slnx          # unit + Testcontainers-backed Postgres/RabbitMQ tests (needs Docker)

cd dashboard-web && npm install && npx ng test --watch=false && npx ng build
```

**Run it.** Bring the backend up first — the dashboard is a pure client of the API and has nothing to
show until sagas exist:

```bash
docker compose up -d --build     # Postgres + RabbitMQ + dashboard API + OrderProcessing sample
curl http://localhost:5080/health
curl -H "X-Api-Key: dev-local-only-change-me" http://localhost:5080/api/sagas
```

Then serve the dashboard UI. This is a dev server, deliberately not part of `docker-compose.yml`:
nothing in the compose stack serves the SPA, so `ng build` alone gets you a bundle in `dist/` that
nothing is hosting.

```bash
cd dashboard-web && npx ng serve     # http://localhost:4200
```

Port 4200 is not incidental — it is the origin `docker-compose.yml` grants CORS access via
`Dashboard__WebOrigin`. Serving the SPA anywhere else means the API rejects its requests until you
change that value to match.

| What | Where | Notes |
| --- | --- | --- |
| Dashboard UI | http://localhost:4200 | `ng serve`; must match `Dashboard__WebOrigin` |
| Dashboard API | http://localhost:5080 | API key `dev-local-only-change-me` (see "Dashboard API authentication") |
| RabbitMQ management | http://localhost:15672 | `guest` / `guest` |
| Postgres | `localhost:5433` | `postgres`/`postgres`, database `vsaga`. Host port is 5433, not 5432, so it can't clash with a Postgres you already run locally |

The `OrderProcessing` sample submits orders on a loop as soon as it starts, so the saga list fills on
its own — there is nothing to trigger by hand.

Note on the Postgres volume: the original one was created under the old `EnsureCreatedAsync()` schema
bootstrap. If yours predates the migrations pass, run `docker compose down -v` once before
`docker compose up` so `MigrateAsync()` isn't applied against an untracked schema. This does **not**
apply to a volume that has already had the versioned migrations applied — those upgrade in place, and
wiping one only costs you your saga history.
