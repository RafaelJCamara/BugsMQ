# Dashboard

`VSaga.Dashboard.Api` (ASP.NET Core Minimal API + SignalR) and `typescript/dashboard-web` (Angular
21 SPA) together form a saga-type-agnostic ops dashboard: list/filter/search every saga instance
across every registered saga type, drill into one instance's timeline or a visual service map, and
manually retry a failed or timed-out saga — all against the same `ISagaSummaryReader`/
`ISagaEventLogStore` contracts every persistence provider implements, so the dashboard needs no
knowledge of any specific saga definition.

## API endpoints

All routes below require authentication (see [Authentication](#authentication)) except `/health`.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/sagas` | Paginated, filterable saga list. Query params: `status`, `sagaType`, `kind`, `search`, `page` (default 1), `pageSize` (default 25), `sortBy`, `sortDescending`. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}` | One instance's summary plus its raw state `DataJson`. `404` if not found. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/timeline` | The full, ordered `SagaLogEntry` history for one instance. |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/map` | The Saga Map for one instance — see [below](#saga-map). |
| `GET` | `/api/sagas/{sagaType}/{correlationId}/children` | Every saga this instance started via `StartChildAsync`. Empty (not `404`) for both "no children" and "no such saga" — the caller already has the plain `GET` above to tell those apart. |
| `POST` | `/api/sagas/{sagaType}/{correlationId}/retry` | Manually redrives a `Failed`/`TimedOut` instance — see [Manual retry](#manual-retry). `409` for any other status. |
| `GET` | `/api/saga-types` | Every distinct saga type currently known to the store, for populating filter dropdowns. |
| `GET` | `/api/correlations/{correlationId}` | Every saga instance — of any type — currently tracking this correlation id. The one route that still takes a bare correlation id, since two saga types (an orchestrated one and a choreography observing the same transaction, or a parent and its child sharing an id — see [`concepts.md`](concepts.md#saga-instances-and-identity)) may both track it. Does **not** include sub-saga children, which have their own correlation ids and are reached via `/children` instead. |
| `GET` | `/health` | Unauthenticated. Real Postgres/RabbitMQ connectivity checks — `503` with a per-check breakdown when either is unreachable, not a hardcoded `200`. |

Every per-instance route is keyed by `(sagaType, correlationId)`, not correlation id alone — see
[`concepts.md`](concepts.md#saga-instances-and-identity) for why.

### Manual retry

Two distinct redrive shapes, chosen automatically from the instance's own timeline:

1. **A technical failure** (an action threw) — the last `StepFailed` entry carries the exact message
   that failed; retry replays just that message against the saga's current, unchanged state.
2. **A business failure or timeout** (the saga reached `Failed`/`TimedOut` through a normal,
   successful step transition, or a timeout, with no `StepFailed` entry at all) — retry resets the
   saga back to its initial state and replays the message that originally started it.

The redrive republishes the original message with a **fresh message id** (so the duplicate check
doesn't discard it) under the **same correlation id**, via `IMessageTransport.PublishRawAsync` — the
dashboard never needs to know the saga's `TState` or definition; whichever process actually runs that
saga's engine picks the republish up through its own normal subscription. Because the republish is
still correlation-id-addressed, every saga type subscribed to that message type sees it, not only the
one being retried — the same fan-out an original delivery has.

## Authentication

A single shared API key (`Dashboard:ApiKey` in configuration) — chosen over JWT/OIDC or basic auth as
the right fit for an internal ops dashboard with no existing identity infrastructure.
`ApiKeyAuthenticationHandler` checks, in order:

1. The `X-Api-Key` header.
2. An `Authorization: Bearer <key>` header.
3. The `?access_token=` query string.

All three are needed because a SignalR hub connection has two legs with different constraints: the JS
client's `accessTokenFactory` sends the token as `Authorization: Bearer` on the negotiate HTTP call,
and falls back to the query string only for the actual WebSocket/SSE upgrade (which can't carry custom
headers).

**Fails closed.** An unconfigured `Dashboard:ApiKey` denies every authenticated request rather than
silently disabling auth. `/health` stays unauthenticated, per standard infra-probe convention.

**Client wiring.** The Angular app sends the key via an `HttpInterceptorFn`
(`typescript/dashboard-web/src/app/interceptors/api-key.interceptor.ts`) on ordinary HTTP calls, and
via the hub connection's own `accessTokenFactory` for SignalR.

**Known limitation, accepted as part of this choice:** a key embedded in a compiled SPA bundle is
visible via browser devtools. This closes off unauthenticated direct API access; it is not per-user
authentication or authorization.

## Live updates (SignalR)

`SagaHub` groups connections per saga instance (`saga:{sagaType}:{correlationId}`) and per list view,
so a detail page only receives updates for the instance it's actually viewing. Two paths push into it:

- **In-process** (`SignalRSagaChangeNotifier`) — used when the hub and the saga engine share a
  process.
- **Cross-process** (`SagaChangePollingService`) — the path that actually delivers live updates in
  the deployed topology, since sagas normally run in a separate process (e.g. `OrderProcessing`) from
  the dashboard API. A background timer diffs the store since its last watermark and pushes the
  difference; the watermark only advances after a successful push, so a tick that throws retries the
  same window on its next tick instead of skipping past it.

`TimelineEntryAdded` events carry the saga type as a leading argument, and the saga-list update event
(`SagaUpdated`) reaches both the list group and the specific instance's group.

## The SPA

`typescript/dashboard-web` (Angular 21) is a saga-type-agnostic client: a list view (paginated,
filterable by status/type/kind/search, sortable by Status/Updated — sorting and paging are both
pushed to the backend query, not applied client-side to whatever page happens to be loaded) and a
detail view with three tabs — Timeline, Data, and Map. The detail page also resolves its own
correlation id through `GET /api/correlations/{id}` and, when more than one saga instance shares it,
renders an "Also tracking this correlation id" strip linking to each sibling (a snapshot, refreshed
only when the current instance itself updates — not independently live-pushed).

It is deliberately **not** an npm-workspace member of `typescript/`'s own workspace (the TypeScript
SDK packages) — see [`typescript-participants.md`](typescript-participants.md) for why the two
toolchains are kept separate. It is not part of `docker-compose.yml`: run it with `npx ng serve` (see
[`getting-started.md`](getting-started.md)) against the containerized API.

## Saga Map

A third tab (alongside Timeline/Data) on the saga detail page renders an Azure-App-Map-style service
graph for one saga instance: nodes are the services involved (Initiator, Orchestrator, Participant, or
Unresolved), edges are the messages that flowed between them, plus a scrubber/replay animation that
steps through the saga's timeline at adjustable speed.

`SagaMapBuilder` (`dotnet/src/VSaga.Dashboard.Api/SagaMapBuilder.cs`) is a pure, unit-testable function
from a saga's raw event log plus a topology registry to nodes/edges/a replay script — it has no
dependency on any specific saga definition, matching the dashboard's saga-type-agnostic design.

**How service identity is tracked.** `MessageEnvelope.From` stamps `x-vsaga-source-service` and
`x-vsaga-causation-id` on every outbound message; `SagaOrchestrator` reads both back off a received
message and stamps them onto `SagaLogEntry.SourceService`/`CausationId` for both `SagaStarted` and
`MessageReceived` entries. `SagaMapBuilder` stitches an edge by matching an outbound entry's
`MessageId` to a later inbound entry's `CausationId`. An outbound message with no matching reply
resolves its destination from `IServiceTopologyStore` (populated by `TopologyRecordingTransport`
observing real `SubscribeAsync` calls across the fleet) — or renders as an "unresolved" placeholder if
even that doesn't know it — and is marked **unanswered** rather than dropped, since a hung downstream
service is often the most useful thing the map can show.

**Failure detection covers two shapes:** a `StepFailed` entry (an action threw), and a business
failure reached through a normal, successful step transition with no exception at all (e.g. "payment
declined") — detected as the last inbound message before a `SagaCompleted` entry on a saga that ended
`Failed`/`TimedOut`.

**Compensation edges are flagged.** Both outbound and inbound edges on the map carry an
`isCompensation` flag, so a compensating REST call's reply (which a fire-and-forget broker
compensation never produces, but a `.CallHttp`/`ctx.CallHttpAsync` compensating call does) renders
distinctly from a forward-flow edge.

**`.CallHttp`/`ctx.CallHttpAsync` hops get their own map entries**, written directly through the
internal `ISagaContextLogSink` naming the called host as the service — a naive loopback via
`ctx.PublishAsync` would stamp the *inbound* message's causation id rather than the outbound call's
own, missing the stitch and showing a bogus self-loop instead of the REST endpoint actually called.

See [`../history/`](history/) for the live-verification history behind each of these mechanisms — most
of them were built once, found wrong by a real `docker compose up` run (not a unit test with a
hand-seeded `SagaLogEntry`), and fixed.
