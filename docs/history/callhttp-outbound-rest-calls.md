# History: outbound REST calls from a saga step, `.CallHttp` (Phase 2 of HTTP-based sagas)

> Preserved verbatim from the original `README.md`. Describes commits `937243a` ("Add
> ISagaContext.PublishAfterCommitAsync, landed and verified alone (§5.1)"), `ee8e108` ("Add
> src/VSaga.Http (.CallHttp DSL) and the Saga Map fix (§5.2, §5.3)"), `e6b7704` ("Add
> tests/VSaga.Http.Tests for .CallHttp, mutation-tested (§5.4)"), and `d95fb5b` ("Live-verify
> .CallHttp end to end; document and ship Phase 2"). See [`../saga-dsl.md`](../saga-dsl.md) for the
> current reference documentation of `.CallHttp`.

---

## Outbound REST calls from a saga step: `.CallHttp`

Phase 2 of [`docs/design/http-based-sagas.md`](../design/http-based-sagas.md) (§5) — a different problem from the
adapter above despite sharing the name HTTP, per the design doc's own §1: a saga step calling an
*ordinary* REST API (a payment gateway, an internal service that never grew a queue consumer), not two
vSaga services talking without a broker. Transport-agnostic — a RabbitMQ-hosted saga gets `.CallHttp`
for free, and `VSaga.Core` gains no `HttpClient` dependency.

**The engine change first, landed and live-verified alone.** `ISagaContext.PublishAfterCommitAsync<T>`
is a default interface method (default body `PublishAsync`) that `SagaContext` overrides to queue
instead of sending immediately; `SagaOrchestrator.HandleStepSuccessAsync` drains the queue sequentially
once `PersistAsync` has committed the step's own transition. Needed because a step that makes a
synchronous call and immediately publishes its own mapped result would let that message re-enter the
same saga instance before its own optimistic-concurrency check has committed — the same class of race
the README's sub-saga sections document, but hit on *every* `.CallHttp` call rather than only on an
unlucky interleaving. Deliberately opt-in, not a change to what plain `PublishAsync` does: a deferred
publish that fails has nowhere safe to go, so a drain failure is caught, logged, and recorded as a
`DeliveryExhausted` timeline entry rather than thrown, leaving the saga `Running` for its own state
timeout to rescue.

**`dotnet/src/VSaga.Http`** adds `.CallHttp(h => h.Post(url).Body(...)...)` as an extension method on
`EventBuilder`, reached through the same public `Then(...)` seam every outside assembly is limited to —
no change to `VSaga.Core`'s DSL. Two result shapes: *inline* (`.OnSuccess(Action<TState>)`, no loopback,
no race, no map problem — the step's own existing computed `.TransitionTo`/`.Finalize` selectors decide
the outcome) and *message loopback* (`.OnSuccess<TOut>()`/`.OnStatus(code).As<TOut>()`/
`.OnFailure<TOut>()`, deserializing the response body as `TOut` and publishing it via
`PublishAfterCommitAsync`, never `PublishAsync`). `.Retry()` is a documented trap here — it replays every
one of a step's actions from index 0, re-POSTing the call — so `.CallHttp` has its own
`.WithRetry(maxAttempts, delay)`, which only retries a genuine network-level failure; a definitive HTTP
response, even a 5xx, is never retried.

**The Saga Map needed its own fix.** A naive loopback via `ctx.PublishAsync` stamps the *inbound*
message's causation id rather than the outbound call's own id, so the stitch misses, the outbound entry
resolves to the saga's own type (it's subscribed to its own loopback message) as a bogus unanswered
self-loop, and the REST endpoint that was actually called never appears as a node. `HttpCallDefinition`
writes its own outbound/inbound timeline entries through the internal `ISagaContextLogSink` — the same
seam `VSaga.Dashboard.Api.Tests` gets for `SagaChangePollingService` — naming the HTTP host as the
service, independent of whatever `PublishAfterCommitAsync`'s own auto-logged entry does for a loopback
outcome.

**Live-verified** against the existing `docker compose up` stack (no new infrastructure): a new
`LoyaltyLookupSaga` reacts to the same `LoyaltyPointsAwarded` event `PostShipmentChoreography` already
tracks (ordinary fan-out, a second independent subscriber) and calls a plain Minimal API endpoint,
`/loyalty/lookup`, added to the sample with no vSaga awareness at all and a simulated ~15% failure rate
so both result shapes fire for real.

- Of 26 instances observed, 23 completed via the loopback shape and 3 failed via the inline shape — close
  to the configured rate, not a transport-specific skew.
- **The engine change held under real traffic first**, verified in isolation before any `.CallHttp` code
  existed: the full suite green, the two pinned race tests unchanged, and the sample's sub-saga
  composition (`StartChildAsync`/`NotifyParentAsync`, §3.1's own race class) completing correctly across
  a fresh run.
- **The Map renders the fix correctly for both result shapes.** A completed (loopback) instance shows
  `localhost` as its own `Participant` node with a stitched, non-`unanswered` request/reply pair, plus a
  separate, expected `LoyaltyLookupSaga → LoyaltyLookupSaga` self-loop from `PublishAfterCommitAsync`'s
  own auto-logged entry for the internal republish — a pre-existing characteristic of that logging, not
  something this fix touches, and it doesn't obscure the real HTTP hop. A failed (inline) instance is
  cleaner still: no self-loop at all (inline never publishes anything), just the request/reply pair to
  `localhost`, correctly marked `failed`.
- Zero unhandled exceptions and zero `DeliveryExhausted` entries across the run (nothing in this demo
  exercises the drain-failure path).
