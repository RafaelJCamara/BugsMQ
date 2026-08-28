# History: mixed sagas shipped (RabbitMQ messages and REST calls in one saga)

> Preserved verbatim from the original `README.md`. Describes five commits landing
> [`docs/design/mixed-sagas.md`](../design/mixed-sagas.md) (added in `a3f63c6`): `3b6bd28` ("Add
> ctx.CallHttpAsync"), `80ca180` ("Fix retried step re-queuing its loopback N times"), `859063c`
> ("Drain the timeout path's deferred publishes"), `5b983c9` ("Render compensating replies as
> compensation edges on the Saga Map"), and `32eb60d` ("Add MixedFulfilmentSaga: RabbitMQ + REST in
> one saga"), documented as shipped in `a10549a`. See [`../saga-dsl.md`](../saga-dsl.md)
> (`ctx.CallHttpAsync`) for the current reference documentation.

---

## Mixed sagas: RabbitMQ messages and REST calls in one saga

[`docs/design/mixed-sagas.md`](../design/mixed-sagas.md) — a saga that drives a broker participant and a REST
participant side by side, and whose compensation unwinds both kinds of hop. Built as five commits, the
first three landing engine-only changes (each live-verified as a regression check before the sample
existed to exercise them), then the Saga Map fix, then the sample itself.

**`ctx.CallHttpAsync(...)`** is the imperative counterpart to `.CallHttp`, reachable from a
`Compensate(state, ...)` delegate or a `WithTimeout(..., t => t.Then(...))` step — neither hands a step
an inbound message the way `EventBuilder` does, so `.CallHttp`'s own extension-method seam doesn't reach
them. `HttpCallDefinition` was split into a shared `HttpCallExecutor<TState>` (URL, retry, status
mapping — everything except how the request body is supplied) plus a thin per-entry-point adapter, so
`.CallHttp` and `ctx.CallHttpAsync` share one execution path bit-for-bit; `.CallHttp`'s existing six
tests stayed green unedited throughout the refactor.

**Two engine bugs, invisible until a saga had both a broker hop and a REST hop in one step:**

- A step's queued `ctx.PublishAfterCommitAsync` call survived a `.Retry()`'d replay uncleared, so a step
  containing both a `.CallHttp` loopback and a `.Publish` under retry would queue one copy of the
  loopback per attempt — each with a fresh `MessageId`, invisible to the duplicate check that only knows
  how to recognize a redelivered *inbound* message. `StepExecutor` now clears the queue in its retry
  catch, before the backoff delay.
- `SagaOrchestrator.HandleTimeoutAsync` persisted a timeout's own transition the same two-phase way
  `HandleStepSuccessAsync` does, but never drained the deferred-publish queue that persist commits —
  `LoyaltyLookupSaga` never hit this because it has no timeout at all. A mixed saga's compensation runs
  from both a message-triggered step and a timeout via the same delegate, so this had to land before the
  sample could exist. Draining now happens after the timeout's final persist and before recording the
  outcome; on a lost persist race, the queued publish is discarded (one `DeliveryExhausted` entry per
  message, naming its type) rather than announcing a transition that was never committed.

**The Saga Map's compensation flag reached inbound edges.** `SagaMapBuilder.ProcessInboundEntry`
hardcoded `IsCompensation: false`, while the outbound side already honoured it — invisible until a
compensating call produced a reply of its own, which a fire-and-forget broker compensation never does but
a compensating REST call does.

**`MixedFulfilmentSaga`**, alongside `OrderSaga` and `LoyaltyLookupSaga` in the same sample: authorizes
payment over REST, reserves stock over the broker (a new `StockService` participant, kept separate from
`InventoryService` so the map stays legible), and on stock failure or timeout releases the stock over the
broker *and* voids the authorization over REST — waiting for the void's own loopback to confirm before
declaring itself `Failed`. That wait (`Voiding`, non-terminal) exists because a compensating loopback that
transitioned straight into a `Finalize` step would let the drained reply resurrect an already-terminal
saga instance (`RunStepAsync` unconditionally flips `Failed` back to `Running` on any redelivery).

**Live-verified** against the default `docker compose` stack (no new overlay — the broker hop rides the
existing RabbitMQ track, the REST hop is transport-agnostic) across 52+ instances, plus a confirmation run
on the HTTP-transport overlay:

- Every outcome branch observed: completed, failed via a 402 decline, failed via `StockUnavailable` with
  a confirmed void, and timed out via `Voiding`'s own backstop (the void call's `.OnFailure` fired with
  no loopback, so nothing but the backstop timeout could move it to terminal). Zero instances left
  sitting in `Voiding`.
- A compensated instance's map shows the REST host as its own `Participant` node with stitched,
  non-`unanswered` request/reply pairs for *both* `/payments/authorize` and `/payments/void`,
  `StockService` as a separate node, and the void + `ReleaseStock` edges rendered `isCompensation: true`.
- Under the chaos overlay, one instance hit `AwaitingStock`'s own timeout directly (not a
  `StockUnavailable` message) and still resolved cleanly: `TimeoutFired` → compensation runs → the
  drained `PaymentVoided` loopback re-enters → `SagaCompleted` — the direct live proof that the timeout
  drain fix above actually closes the gap it was built for. Zero `DeliveryExhausted` entries and zero new
  `UnexpectedEvent` entries on `OrderSaga` across the run (the sample's few pre-existing `UnexpectedEvent`
  entries are unrelated redelivery-duplicate noise on `OrderSaga`'s own message types, not cross-talk from
  the new ones).
