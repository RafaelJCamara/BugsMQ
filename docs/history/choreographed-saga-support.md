# History: choreographed saga support

> Preserved verbatim from the original `README.md`. Describes commit `0c5fe38` ("Add choreographed
> saga DSL, closing the SagaKind.Choreographed gap"). See [`../saga-dsl.md`](../saga-dsl.md) and
> [`../concepts.md`](../concepts.md) for the current reference documentation of
> `ChoreographedSagaDefinition`.

---

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

**Test coverage:** `dotnet/tests/VSaga.Core.Tests/TestShippingChoreography.cs` (a fixture) and
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

> **Resolved in the next pass** — see [`saga-identity-scoping.md`](saga-identity-scoping.md) below, which makes the
> composite key real end to end. The follow-on it names (actually wiring a choreographed saga into the
> `OrderProcessing` sample) is still open.
