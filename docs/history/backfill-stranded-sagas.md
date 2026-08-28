# History: backfilling the 60 stranded sagas

> Preserved verbatim from the original `README.md`. Describes commit `f412675` ("Backfill the 60
> stranded OrderSaga instances").

---

## Backfilling the 60 stranded sagas

The separate backfill the section above deferred. `dotnet/tools/BackfillStrandedTimeouts` is a small one-time
console tool, not a new engine capability or dashboard endpoint — this is a known, bounded fix for two
known states on one known saga type, not a general problem worth new public surface. It reaches directly
into `VSagaDbContext` (ops code against the one concrete provider actually deployed here, not pluggable
engine code, so there's no reason to add new abstraction-interface surface for it) rather than adding a
query method to `ISagaTimeoutStore`, which today only exposes `ScheduleAsync`/`CancelAsync`/`ClaimDueAsync`
— no way to ask "does a pending timeout already exist for this instance", which the tool needs for its
dedup guard.

**The fix reuses the real engine path rather than hand-rolling the unwind.** For every `OrderSaga`
instance `Status == Running` in `AwaitingInventory`/`AwaitingShipment` with no existing pending
`SagaTimeouts` row for that exact state (the dedup guard — an instance that entered either state *after*
the live timeout-coverage fix above already has a normal one, and scheduling a second would be sloppy
even though harmless: `HandleTimeoutAsync`'s own `CurrentState != ForState` guard no-ops the stale one
rather than double-compensating), the tool inserts a `SagaTimeouts` row due immediately. It writes no
compensation or finalization logic of its own — the already-running `SagaTimeoutDispatcherHostedService`
claims the row on its normal 5s poll and runs it through the exact same `SagaOrchestrator.HandleTimeoutAsync`
path a real timeout takes, compensating and finalizing `TimedOut` exactly as the live fix above already
does for new instances.

**Live verification**, under `docker compose up --build` against the real Postgres volume that actually
held the historical stranded rows:

- Pre-run count, confirmed live via direct SQL before touching anything: exactly 60 — 43
  `AwaitingInventory`, 17 `AwaitingShipment`. No drift from the historical number above.
- Ran the tool: scheduled exactly 60 backfill timeouts, all sharing one `DueAtUtc` batch timestamp (a
  useful side effect — the sample's own continuous order load was concurrently creating unrelated fresh
  timeout rows, so a loose "recent `DueAtUtc`" filter over-matched; the shared exact timestamp was the
  reliable way to isolate the backfilled rows afterward).
- ~15s later — one dispatcher poll cycle — all 60 rows had fired and all 60 instances were
  `CurrentState=Failed`, `Status=TimedOut` (43/43, 17/17).
- One instance from each state traced end to end through `SagaEventLog`: both show
  `TimeoutFired → CompensationStarted → MessagePublished(...) → CompensationStepSucceeded →
  StepSucceeded (→ Failed)`, identical in shape to an ordinary timeout — nothing about being backfilled
  is visible on the timeline. The `AwaitingShipment` trace additionally confirms `Compensate()`'s
  most-recent-first order in practice, not just in the DSL: `CompensationStepSucceeded fromState=
  AwaitingPayment` logged before `fromState=AwaitingInventory` — refund, then release, exactly the order
  `ConfigureRecovery` registers them against visited-state history.
- Cross-checked one resolved instance via the live dashboard API (`GET /api/sagas/OrderSaga/{id}`) —
  matches the database exactly.
- **Idempotent.** Re-ran the tool immediately after: found 0 stranded, scheduled 0. Confirmed again on a
  later pass: 0 `OrderSaga` instances stranded in either state, and none have reappeared since.

**Not mutation-tested**, unlike this repo's usual habit for anything envelope/header/linkage-adjacent —
deliberately: the tool contains no new engine logic to mutate. Its only real claim is "insert a row with
these five columns," and the correctness that matters is entirely in `SagaOrchestrator.HandleTimeoutAsync`,
which is already exercised by the engine's own test suite and by every other live timeout in this README.
