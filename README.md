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
  `X-Api-Key` header, falling back to the `?access_token=` query string for the SignalR hub (which
  can't attach custom headers to a WebSocket upgrade). It **fails closed**: an unconfigured key
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
