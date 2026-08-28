# History: transport adapter, HTTP (Phase 1 of HTTP-based sagas)

> Preserved verbatim from the original `README.md`. Describes commits `d69c9c1` ("Add
> VSaga.Transport.Http (Phase 1 of HTTP-based sagas, §4)"), `b60ce59` ("Add VSaga.Transport.Http.Tests
> and make the local-dispatch pump concurrent"), `06f79aa` ("Wire the HTTP transport into the sample
> and dashboard (§4.6)"), and `6f7bac6` ("Fix cross-process gate deadlock found by live HTTP
> verification; document and ship Phase 1"), following the design doc added in `81674ec` ("Add
> HTTP-based sagas design doc"). See [`../transports/http.md`](../transports/http.md) for the current
> reference documentation.

---

## Transport adapter: HTTP

`VSaga.Transport.Http` implements `IMessageTransport` over plain HTTP with **no broker at all** —
Phase 1 of [`docs/design/http-based-sagas.md`](../design/http-based-sagas.md), whose §3 traces three engine
constraints an obvious implementation would have gotten wrong, and whose live-verification pass found a
fourth. `PublishAsync`/`SendAsync` POST a header-based wire format (`x-vsaga-message-type`,
`-correlation-id`, `-message-id`, plus the four `MessageEnvelope` headers) to configured peer endpoints;
a `200` response with that same header set *is* the reply, fed back into whichever local subscriber its
own type resolves to. `dotnet/src/VSaga.Transport.Http/HttpMessageTransport.cs` and `HttpInboundDispatcher.cs`
are the whole adapter; `ServiceCollectionExtensions.AddVSagaHttp` wraps it in the same
`MiddlewarePipelineTransport` every other adapter shares.

**Two mechanisms carry the whole design, both driven by `HttpInboundDispatcher`:**

- *A per-correlation dispatch gate* — every local dispatch (a genuine inbound request, a same-process
  publish, or a captured reply) serializes against every other dispatch for the same correlation id, so
  a reply can never re-enter a saga while its own publishing step is still persisting.
- *An ambient (`AsyncLocal`) `SyncReplyCollector`*, installed only around a genuine inbound request, that
  captures a handler's own publish as that request's synchronous reply exactly when the publish resolves
  to **no destination** — never by matching correlation id, which this repo's own `OrderSaga` sample
  breaks (`ShipOrder` is published under the saga's own correlation id from inside a reply handler, and
  has a real route, so it must go out as a normal POST, not be swallowed as that handler's reply).

**Found live, not by the unit suite: a fan-out reply that routes back to its own originating service can
deadlock that service's gate against itself.** `OrderShipped` has to reach both its three local
choreography participants *and* back to the saga host (§4.3's fan-out routing) — so when the saga host's
own dispatch (handling `PaymentCharged`, the second of two parallel branches) is still holding its
correlation gate while awaiting `ShipOrder`'s HTTP response, and the participant's reply to `ShipOrder`
is `OrderShipped` routing back to that same saga host, the inbound `OrderShipped` request cannot acquire
the very gate the outbound `ShipOrder` call is blocked behind. A genuine cross-process circular wait,
breakable only by a timeout — live traffic hit this on effectively every order that reached shipping,
each one blocking for the full 30s `RequestTimeout` before failing outright. Fixed by bounding the
inline path's own gate acquisition (`InlineGateAcquireTimeout`, 5s default,
`HttpInboundDispatcher.DispatchInlineAsync`) and falling back — on timeout only — to the same
deferred-to-the-pump path a captured reply already uses: `202` now, dispatched once the gate frees,
lossless rather than a 30-second block. This is the fourth instance in this repo of a defect "caught
only by a live run, never by tests that hand-built the objects under test" (§3.3b's own words about the
third).

**Live-verified**, project name `vsaga-http`, `docker compose -p vsaga-http -f docker-compose.yml -f
docker-compose.http.yml up -d --build`. `order-processing` (Role=Sagas) and a new
`order-processing-participants` (Role=Participants) container split the one sample image in two — see
docker-compose.http.yml's own comments for why local subscriptions counting as routes (§3.3a) forced
this rather than a same-process run. Brought up cold; postgres/rabbitmq/dashboard-api healthy within
seconds, both order-processing containers started immediately after.

- **The broker is out of the message path, by traffic, not absence** — `rabbitmq` stays in the compose
  stack (`dashboard-api`'s own health check needs it) but with dozens of orders processed end to end,
  its management API reports `GET /api/queues` → `[]` and `GET /api/overview`'s `message_stats` → `{}`:
  zero queues ever declared, zero messages ever published or delivered, for the whole run.
- **The Saga Map stitches request→reply correctly over the HTTP hop.** A completed order's map
  (`GET /api/sagas/OrderSaga/{id}/map`) shows real service nodes — `OrderSubmitter`, `InventoryService`,
  `PaymentService`, `ShippingService` — each with a real edge back to `OrderSaga`, not
  `unresolved:{MessageType}`: proof both that `x-vsaga-source-service`/`x-vsaga-causation-id` survived
  the HTTP hop (the exact pair §3.3b and the earlier `CausationId` story already broke on) and that the
  participants container's own topology recording is registered (`AddVSagaEfCore` +
  `AddVSagaTopologyRecording`, both roles, per docker-compose.http.yml's own note).
- **Manual retry works over this transport.** A `Failed` `OrderSaga` instance, redriven via
  `POST /api/sagas/OrderSaga/{id}/retry` (`202`), completed successfully on replay — proving
  `VSaga.Dashboard.Api`'s wildcard `Http:Routes:"*"` route (its own fix, below) actually reaches the
  saga host, not just that the endpoint returns a status code.
- **After the deadlock fix, order outcomes matched the sample's own built-in failure rates**: of a
  30-order sample, 20 `Completed`, 9 `Failed` (card declines / stock-outs / carrier rejections), 1
  `TimedOut` — the same shape as every other transport's live pass, not a transport-specific skew.
- **Not re-run this pass**: the chaos overlay (`docker-compose.chaos.yml`) against this track. Nothing
  in the chaos-fault injection path is HTTP-specific (`MiddlewarePipelineTransport` wraps `HttpMessageTransport`
  identically to every other adapter), but it hasn't been independently live-verified over HTTP yet —
  noted here rather than silently assumed.

**`VSaga.Dashboard.Api` is now transport-switchable, not unconditionally RabbitMQ.** Same
`Transport:Provider` switch as the sample; the `Http` case binds a single wildcard route
(`Http:Routes:"*"` in `ConfigHttpRouteTable`) to the saga host, since `/retry`'s `PublishRawAsync` never
knows its message type ahead of time and — unlike the sample's own per-command routing — has no fixed
universe of types to enumerate. This was a pre-existing gap (`/retry` already misbehaved on Wolverine
and MassTransit, whose wire formats differ from the RabbitMQ one it was hardcoded to) that HTTP is only
the first configuration to fix, not the first to have. Making the registration conditional had a second
effect for free: `RabbitMqHealthCheck` already returns `Healthy("No message broker configured.")` when
`RabbitMqConnectionManager` isn't registered, so the `/health` endpoint stopped being unconditionally
RabbitMQ-shaped with no change to the health check registration itself.

**Known, deliberate limitations, not defects:** the local-dispatch channel is in-process and not
durable — a crash between an HTTP response and its dispatch loses that reply, covered by the saga's own
state timeout, the same safety net that already covers a lost broker message. Sync request/response
serializes what was parallel fan-out over every other transport (`ReserveInventory`/`ChargePayment`
become two blocking round trips, not two fire-and-forget publishes). Both are inherent to the
synchronous-request/response delivery model this phase deliberately chose (see the design doc's §1 and
§7), not gaps in this implementation of it.
