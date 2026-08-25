# BugsMQ

BugsMQ is an orchestration-first saga library for .NET 10, built directly on `RabbitMQ.Client`
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
  BugsMQ.Abstractions             Contracts only: saga/transport/persistence interfaces, no implementation deps
  BugsMQ.Chaos                    Opt-in fault-injection transport middleware (delay/drop/duplicate)
  BugsMQ.Core                     The saga engine: fluent DSL + orchestrator runtime
  BugsMQ.Observability            OpenTelemetry hosting extensions
  BugsMQ.Persistence.EFCore       EF Core store implementations (provider-agnostic)
  BugsMQ.Persistence.EFCore.Postgres   Postgres-specific EF Core migrations (see "EF Core migrations" below)
  BugsMQ.Persistence.InMemory     In-memory store implementations (dev/test)
  BugsMQ.Testing                  SagaTestHarness for unit-testing saga definitions
  BugsMQ.Transport.InMemory       In-memory IMessageTransport (dev/test)
  BugsMQ.Transport.RabbitMQ       Real IMessageTransport over RabbitMQ.Client
  BugsMQ.Dashboard.Api            ASP.NET Core API + SignalR hub for the ops dashboard
dashboard-web/                    Angular 21 SPA for the dashboard (list/detail, live updates)
samples/
  BugsMQ.Samples.OrderProcessing(.Contracts)   End-to-end reference saga + participants
tests/                            One test project per major src/ project
docker-compose.yml                Postgres + RabbitMQ + dashboard-api + the OrderProcessing sample
```

## Current state

Two commits exist before this pass (`a17f31d` "Add BugsMQ v1", `b1ff30b` "Add CI workflow and
expand test coverage"), plus an in-progress, previously-uncommitted code-quality pass (analyzers:
SonarAnalyzer.CSharp, Meziantou.Analyzer, AsyncFixer; `TreatWarningsAsErrors=true`; an expanded
`.editorconfig`) that this commit finishes and verifies alongside the production-hardening work
described below.

**What's implemented and tested:** orchestrated sagas (linear state machines with compensation,
step-level retry policies, per-state timeouts, manual whole-saga retry), choreographed sagas (see
"Choreographed saga support" below), EF Core/Postgres and in-memory persistence, the RabbitMQ and
in-memory transports, the dashboard API/SPA, and the `BugsMQ.Testing` harness. The OrderProcessing
sample runs both saga kinds side by side against real Postgres/RabbitMQ via `docker-compose.yml` —
`OrderSaga` (orchestrated: compensation, timeouts) and `PostShipmentChoreography` (choreographed:
an independent fan-out tracked under the same correlation id) — see "Choreography in the
OrderProcessing sample" below.

**What's deliberately out of scope for v1** (per the original commit's own roadmap note, not
addressed in this pass): additional transport adapters (MassTransit/Wolverine) and sub-saga
composition. Parallel/fan-out saga steps, also on that original list, are covered as of "Parallel
fan-out and join" below. The SignalR hub and polling service, listed here as an untested gap through
several passes, are covered as of "SignalR hub and polling service tests".

Chaos-engineering transport middleware — listed as out of scope in the original v1 roadmap note,
with the `MessageMiddleware`/`MiddlewarePipelineTransport` seam left in place unused specifically so
it could be added later without touching Core/Abstractions/the transport — is implemented in a
later pass; see "Chaos-engineering transport middleware" below.

## Production-hardening pass (this commit)

A full-codebase gap analysis found several v1 gaps explicitly flagged in code comments as "out of
scope for v1" or simply absent. The following six were scoped in and are covered by both unit
tests and a live `docker-compose up` verification (real sagas processed end-to-end, migrations
applied cleanly, auth and health checks exercised over HTTP):

- **Dashboard API authentication** — a shared API key (`Dashboard:ApiKey` config), chosen over
  JWT/OIDC or basic auth as the right fit for a v1 internal ops dashboard with no existing identity
  infrastructure. `src/BugsMQ.Dashboard.Api/Auth/ApiKeyAuthenticationHandler.cs` checks the
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
  Postgres/RabbitMQ connectivity checks (`src/BugsMQ.Dashboard.Api/HealthChecks/`), returning `503`
  with a per-check breakdown when either is unreachable. Both checks resolve their dependency
  lazily via `IServiceScopeFactory` rather than a direct constructor dependency, so they degrade to
  "not configured" instead of failing to construct when a test host swaps out the real providers.

- **EF Core migrations** — replaced `EnsureCreatedAsync()` with versioned migrations
  (`db.Database.MigrateAsync()`). The Postgres-specific migration code lives in a new project,
  `BugsMQ.Persistence.EFCore.Postgres`, kept separate from `BugsMQ.Persistence.EFCore` so the
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
(`src/BugsMQ.Dashboard.Api/SagaMapBuilder.cs`) is a pure, unit-testable function from a saga's raw
event log + a topology registry to nodes/edges/a replay script — no dependency on any specific saga
definition, consistent with the dashboard's saga-type-agnostic design.

Service identity travels on `MessageEnvelope` headers (`x-bugsmq-source-service`,
`x-bugsmq-causation-id`, both defined in `MessageEnvelope.From`); `SagaOrchestrator` reads these back
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

A new project, `BugsMQ.Chaos`, plugs three fault types into the `IOutboundMessageMiddleware`/
`IInboundMessageMiddleware` seam that `MiddlewarePipelineTransport` already wraps every transport in
— the seam the original v1 commit left in place unused specifically for this. `AddBugsMqChaos(...)`
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
coverage rather than something `BugsMQ.Chaos` should paper over. **Closed in a later pass** — see
"Timeout coverage for every awaiting state" below; all three awaiting states now carry a timeout.

**Wiring.** `samples/BugsMQ.Samples.OrderProcessing` calls `AddBugsMqChaos` only when
`Chaos:Enabled` is `true` (`appsettings.json` defaults it to `false`, so plain `docker compose up`
is unaffected). `docker-compose.chaos.yml` is an overlay that turns all three faults on with sample
tuned probabilities:

```bash
docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build
```

**Live verification against the real stack caught a real bug in the tuning, and a real bug in
`BugsMQ.Core`.** First pass used `Delay.MaxDelay = 35s`, which is longer than the 8-second order
cadence: `RabbitMqTransport` gives each `SubscribeAsync` call one channel with a single sequential
consumer (`BasicQosAsync(prefetchCount: 32)`, one `ReceivedAsync` handler awaited to completion
before the next delivery), so an inbound delay doesn't just slow down the one delayed message — it
blocks that whole consumer from dispatching anything else while it waits. Watching
`bugsmq.saga.OrderSaga`'s queue depth via the RabbitMQ management API showed it pinned at 32
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

`BugsMQ.Chaos.Tests` covers each fault type in isolation (trigger vs. no-trigger, both directions,
the no-double-ack property of duplicate-inbound, the `RollTrigger`/`NextDelay` probability helpers'
edge cases, and `AddBugsMqChaos`'s registration gating) using the same hand-written-fake xUnit style
as the rest of the repo's tests — no mocking framework, `FakeTimeProvider` for the delay tests
instead of real waits.

## Timeout/message race fix

Closes the race the chaos-testing pass above found but deliberately left unfixed. In
`SagaOrchestrator<TState>.HandleTimeoutAsync` (`src/BugsMQ.Core/Runtime/SagaOrchestrator.cs`), a due
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

`tests/BugsMQ.Core.Tests/SagaOrchestratorTimeoutRaceTests.cs` covers both windows deterministically —
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
  (`samples/BugsMQ.Samples.OrderProcessing/Participants/ParticipantService.cs`), covering all three
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
`BugsMQ.Core.Dsl.OrchestratedSagaDefinition<TState>` was the only `ISagaDefinition<TState>`
implementation in the codebase.

**What was added:** `BugsMQ.Core.Dsl.ChoreographedSagaDefinition<TState>`, a second fluent DSL base
class alongside the orchestrated one. Investigating the existing engine first showed that
`SagaOrchestrator<TState>`, `SagaRuntime<TState>`, `ServiceCollectionExtensions.AddSaga<TDefinition,
TState>()`, and `BugsMQ.Testing.SagaTestHarness` are all already written purely against
`ISagaDefinition<TState>` — none of them know or care what `Kind` a saga is. That meant this feature is
entirely additive to `BugsMQ.Core.Dsl`; the runtime, persistence, retry dispatcher, timeout dispatcher,
and dashboard needed **zero** changes, and a choreographed saga is registered with the exact same
`services.AddBugsMqEngine(o => o.AddSaga<TDefinition, TState>())` call an orchestrated one uses.

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
Both are now factored into `BugsMQ.Core.Dsl.StepExecutor`/`CompensationRunner`, and
`OrchestratedSagaDefinition` was refactored to call them too (behavior-preserving — the full existing
test suite passes unchanged). `TimeoutBuilder<TState>` was changed to take a compensation-runner
delegate instead of the concrete orchestrated `SagaDefinitionModel<TState>`, so it's now shared as-is by
both DSLs' `WithTimeout(...)`. The two DSLs' public fluent builders (`EventBuilder` vs.
`ChoreographyEventBuilder`) were deliberately kept separate rather than unified behind a common
abstraction — the state-gated chaining `During(...).When<T>()` needs is orchestration-specific, and
forcing it into a shared shape would have leaked that gating concept into choreography's builder.

**Test coverage:** `tests/BugsMQ.Core.Tests/TestShippingChoreography.cs` (a fixture) and
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
decides when the leg is finished. It publishes nothing and commands nobody.

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

**Not wired into the OrderProcessing sample**, deliberately. The obvious demonstration would be
reserving inventory and authorizing payment in parallel rather than in sequence — but `OrderSaga` is
this project's reference for the linear shape, and several README sections describe its exact
compensation ordering and timeout behaviour. Restructuring it is a product decision about what the
sample is *for*, not a detail to fold into the pass that built the primitive. The same split as the
choreographed DSL, which shipped one pass ahead of its sample wiring.

## Getting started

**Build and test:**

```bash
dotnet build BugsMQ.slnx
dotnet test BugsMQ.slnx          # unit + Testcontainers-backed Postgres/RabbitMQ tests (needs Docker)

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
| Postgres | `localhost:5433` | `postgres`/`postgres`, database `bugsmq`. Host port is 5433, not 5432, so it can't clash with a Postgres you already run locally |

The `OrderProcessing` sample submits orders on a loop as soon as it starts, so the saga list fills on
its own — there is nothing to trigger by hand.

Note on the Postgres volume: the original one was created under the old `EnsureCreatedAsync()` schema
bootstrap. If yours predates the migrations pass, run `docker compose down -v` once before
`docker compose up` so `MigrateAsync()` isn't applied against an untracked schema. This does **not**
apply to a volume that has already had the versioned migrations applied — those upgrade in place, and
wiping one only costs you your saga history.
