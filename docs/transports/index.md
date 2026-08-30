# Transports

Every message vSaga sends or receives goes through one `IMessageTransport` implementation
(`VSaga.Abstractions.Transport`). `VSaga.Core` depends on nothing else to move messages, and no
adapter uses the underlying bus's own saga/state-machine or handler-discovery machinery — only its
raw publish/consume primitives. That rule is the load-bearing constraint behind every adapter's design;
see each adapter's own page for how it plays out concretely.

## The contract

```csharp
public interface IMessageTransport
{
    Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken ct = default);
    Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken ct = default);
    Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken ct = default);
    Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken ct = default);
    Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken ct = default);
}
```

- **`PublishAsync`/`SendAsync`** — the typed entry points every saga step actually calls (via
  `ISagaContext`). `Publish` broadcasts to whatever subscribes to the message type; `Send` addresses
  one named destination directly, bypassing topic routing.
- **`PublishRawAsync`/`SendRawAsync`** — untyped counterparts, publishing pre-serialized JSON by
  message-type name rather than CLR type. Used by callers that know a message's stored type name and
  payload but aren't compiled against the assembly that defines it — the dashboard's manual-retry
  endpoint (see [`dashboard.md`](../dashboard.md#manual-retry)) is the main one. `SendRawAsync`
  defaults to falling back to a broadcast via `PublishRawAsync` for an adapter that predates the
  method; every adapter shipped in this repo overrides it with a real addressed send.
- **`SubscribeAsync`** — registers a handler for a runtime-declared set of message types
  (`TransportSubscription`), returning a disposable that stops it. Async because a real broker adapter
  needs to declare exchanges/queues/bindings and start consuming *before returning* — topology must
  exist by the time the call completes, not lazily on first use (a gap the Brighter adapter had to work
  around; see [`brighter.md`](brighter.md)).

Every adapter is wrapped in `MiddlewarePipelineTransport` (`VSaga.Transport.Common`), the shared
decorator that `VSaga.Chaos`'s fault injection and topology recording both plug into — this is why
chaos and the Saga Map's topology registry work identically across every adapter with zero
adapter-specific code.

## Choosing an adapter

| Adapter | Underlying bus | When to reach for it |
| --- | --- | --- |
| [`rabbitmq.md`](rabbitmq.md) | `RabbitMQ.Client` directly | The reference implementation. Default choice for a real broker with no other constraint. |
| [`wolverine.md`](wolverine.md) | WolverineFx.RabbitMQ | Already standardized on Wolverine elsewhere in your stack. |
| [`masstransit.md`](masstransit.md) | MassTransit 8.x + RabbitMQ | Already standardized on MassTransit 8.x (Apache-2.0; v9 is commercially licensed — this adapter is deliberately pinned below `9.0.0`). |
| [`brighter.md`](brighter.md) | Paramore.Brighter's RabbitMQ gateway | Already standardized on Brighter elsewhere in your stack. |
| [`http.md`](http.md) | Plain HTTP, no broker | No broker infrastructure available or wanted at all — trades parallel fan-out for synchronous request/response and an in-process (non-durable) local-dispatch path. |
| [`in-memory.md`](in-memory.md) | None (single process) | Local development and `SagaTestHarness`-based unit testing. Not for production. |

All four RabbitMQ-family adapters (RabbitMQ, Wolverine, MassTransit, Brighter) share one topic
exchange (`vsaga.saga.events` by default) and route by message-type name, so the *shape* of a
deployment looks the same regardless of which one is chosen — see [`configuration.md`](../configuration.md#transport-options)
for each adapter's options.

## Running an adapter's own overlay

RabbitMQ is what plain `docker compose up` runs (see ["Run the demo"](../../README.md#run-the-demo)).
To try Wolverine, MassTransit, Brighter, or HTTP instead, each has its own compose overlay — but unlike
the chaos overlay, these two things are **not optional**:

- **A `-p <project-name>` compose project name.** Without it, this overlay's containers join the
  default `bugsmq`/`vsaga` compose project instead of a distinct one, colliding with any stack already
  up under the plain command.
- **Every host port is remapped** (`!override` in each overlay file) so the overlay's stack can run
  *alongside* the plain one rather than fighting it for `5433`/`5672`/`15672`/`5080`. Skip the `-p` flag
  and you'll bring these containers up fine, then find every URL this repo documents
  (`localhost:5080`, `localhost:15672`, ...) pointing at whichever stack happened to bind the port first.

```bash
docker compose -p vsaga-wolverine    -f docker-compose.yml -f docker-compose.wolverine.yml    up -d --build
docker compose -p vsaga-masstransit  -f docker-compose.yml -f docker-compose.masstransit.yml  up -d --build
docker compose -p vsaga-brighter     -f docker-compose.yml -f docker-compose.brighter.yml     up -d --build
docker compose -p vsaga-http         -f docker-compose.yml -f docker-compose.http.yml         up -d --build
```

Each overlay remaps a different, non-overlapping port range, so more than one can genuinely run at
once — see that overlay's own file header for its exact ports:

| Overlay | Postgres | RabbitMQ (AMQP / mgmt) | Dashboard API |
| --- | --- | --- | --- |
| `docker-compose.wolverine.yml` | `5443` | `5772` / `15772` | `5180` |
| `docker-compose.masstransit.yml` | `5444` | `5872` / `15872` | `5280` |
| `docker-compose.brighter.yml` | `5445` | `5972` / `15972` | `5380` |
| `docker-compose.http.yml` | `5446` | `6072` / `16072` | `5480` |

Tear one down the same way you brought it up, naming the same `-p` project:
`docker compose -p vsaga-wolverine down`.

**Viewing an overlay's dashboard.** The dashboard SPA's API base URL is a hardcoded constant, not an
environment variable — see `API_BASE_URL` (and `DASHBOARD_API_KEY`) in
[`typescript/dashboard-web/src/app/api-config.ts`](../../typescript/dashboard-web/src/app/api-config.ts).
It always points at the plain stack's `5080`. To view a specific overlay's dashboard instead, edit
`API_BASE_URL` to that overlay's Dashboard API port from the table above (and `DASHBOARD_API_KEY` too, if
that overlay's `Dashboard__ApiKey` differs from the default dev value) before running `ng serve`.

## What every adapter guarantees

- **All four vSaga envelope headers round-trip losslessly**: `x-vsaga-source-service`,
  `x-vsaga-causation-id`, `x-vsaga-parent-saga-type`, `x-vsaga-parent-correlation-id` — plus, since
  production-readiness §8.17, the W3C `traceparent`/`tracestate` pair (bare names, not
  `x-vsaga-`-prefixed — see [`../observability.md`](../observability.md#traces)). Every adapter's own
  test suite includes a dedicated round-trip test for these; several were added specifically because an
  earlier version of this repo shipped header-threading code with tests that hand-built the field and
  proved nothing (see [`../history/sub-saga-parent-linkage.md`](../history/sub-saga-parent-linkage.md)).
- **An unroutable publish is detected where the underlying package supports it.** RabbitMQ, MassTransit,
  and HTTP all surface it as `MessageTransportPublishException.IsUnroutable`. Wolverine's and Brighter's
  underlying gateway packages have no equivalent as of the pinned versions — confirmed absent by
  reflecting over the packages' publish paths, not merely undocumented — see [`wolverine.md`](wolverine.md)
  and [`brighter.md`](brighter.md) for the tests that pin the verified absence directly.
- **`SagaOrchestrator` owns all retry/redelivery/dedup**, never the underlying bus's own retry policy —
  every adapter configures the underlying bus for zero (or broker-native at-least-once only) retries so
  it doesn't fight the engine's own bounded, application-level redelivery.
