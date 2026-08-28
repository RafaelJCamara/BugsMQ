# Transport adapter: HTTP

`VSaga.Transport.Http` implements `IMessageTransport` over plain HTTP with **no broker at all** —
Phase 1 of [`../design/http-based-sagas.md`](../design/http-based-sagas.md). `PublishAsync`/`SendAsync`
POST a header-based wire format to configured peer endpoints; a `200` response carrying the same
header set **is** the reply, fed back into whichever local subscriber its type resolves to. Full build
history and live-verification detail:
[`../history/transport-adapter-http.md`](../history/transport-adapter-http.md).

Not to be confused with `.CallHttp`/`ctx.CallHttpAsync` (`VSaga.Http`) — a transport-agnostic saga step
that calls an *ordinary* REST API. This adapter replaces the broker entirely for vSaga-to-vSaga
traffic; `.CallHttp` is for calling something that was never a vSaga participant at all. See
[`saga-dsl.md`](../saga-dsl.md#callhttp-from-vsagahttp) for `.CallHttp`.

## The two mechanisms

`HttpInboundDispatcher` drives both:

- **A per-correlation dispatch gate.** Every local dispatch (a genuine inbound request, a same-process
  publish, or a captured reply) serializes against every other dispatch for the same correlation id, so
  a reply can never re-enter a saga while its own publishing step is still persisting.
- **An ambient (`AsyncLocal`) reply collector**, installed only around a genuine inbound request, that
  captures a handler's own publish as that request's synchronous reply exactly when the publish
  resolves to **no destination** — never by matching correlation id, since a saga can legitimately
  publish something under its own correlation id from inside a reply handler that has a real route and
  must go out as a normal POST.

## A cross-process deadlock, found live

A fan-out reply that routes back to its own originating service can deadlock that service's dispatch
gate against itself: if a saga host's own dispatch is still holding its correlation gate while awaiting
an outbound call's HTTP response, and that call's own reply routes back to the same saga host under the
same correlation id, the inbound reply cannot acquire the very gate the outbound call is blocked
behind — a genuine cross-process circular wait, breakable only by a timeout. Fixed by bounding the
inline dispatch path's own gate-acquisition wait (`InlineGateAcquireTimeout`, 5s default) and falling
back — on timeout only — to the same deferred-to-a-background-pump path a captured reply already uses:
a `202` now, dispatched once the gate frees, lossless rather than a long block.

## Known, deliberate limitations

- **The local-dispatch channel is in-process and not durable.** A crash between an HTTP response and
  its local dispatch loses that reply — covered by the saga's own state timeout, the same safety net
  that already covers a lost broker message on any other adapter.
- **Synchronous request/response serializes what is parallel fan-out on a broker-backed adapter.** Two
  `.Publish(...)` calls that would be two independent fire-and-forget broker publishes become two
  blocking HTTP round trips here. Both limitations are inherent to the synchronous delivery model this
  adapter deliberately chose, not gaps in this implementation of it.

## Unroutable-publish detection

A non-2xx or connection-level failure on the outbound POST is surfaced as
`MessageTransportPublishException`, matching the RabbitMQ adapter's own detection fidelity — at higher
fidelity than the Wolverine and Brighter adapters, whose underlying gateway packages have no
unroutable-return signal at all.

Options: [`../configuration.md#httptransportoptions-vsagatransporthttp`](../configuration.md#httptransportoptions-vsagatransporthttp).
Compose overlay: `docker-compose.http.yml` (splits the sample into separate Sagas/Participants
containers so local-subscription counting as a "route" doesn't collapse into one process).

## TypeScript

`@vsaga/transport-http` is wire-compatible with this adapter for Node participants — see
[`../typescript-participants.md`](../typescript-participants.md).
