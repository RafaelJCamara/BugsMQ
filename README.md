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
step-level retry policies, per-state timeouts, manual whole-saga retry), EF Core/Postgres and
in-memory persistence, the RabbitMQ and in-memory transports, the dashboard API/SPA, and the
`BugsMQ.Testing` harness. The OrderProcessing sample exercises compensation and timeouts against
real Postgres/RabbitMQ via `docker-compose.yml`.

**What's deliberately out of scope for v1** (per the original commit's own roadmap note, not
addressed in this pass): choreographed sagas (`SagaKind.Choreographed` exists as an enum value
only — no DSL/runtime), chaos-engineering transport middleware (the `MessageMiddleware`/
`MiddlewarePipelineTransport` seam exists for it, unused by default), and additional transport
adapters (MassTransit/Wolverine). Also out of scope: parallel/fan-out saga steps, sub-saga
composition, and broader test-coverage expansion (e.g. SignalR hub/polling-service tests) beyond
what's needed to verify the changes below.

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

## Getting started

```bash
dotnet build BugsMQ.slnx
dotnet test BugsMQ.slnx          # unit + Testcontainers-backed Postgres/RabbitMQ tests (needs Docker)

cd dashboard-web && npm install && npx ng test --watch=false && npx ng build

docker compose up -d --build     # Postgres + RabbitMQ + dashboard API + OrderProcessing sample
curl http://localhost:5080/health
curl -H "X-Api-Key: dev-local-only-change-me" http://localhost:5080/api/sagas
```

The docker-compose Postgres volume was created under the old `EnsureCreatedAsync()` schema
bootstrap; if you have one from before this pass, run `docker compose down -v` once before
`docker compose up` so `MigrateAsync()` isn't applied against an untracked schema.
