# History: parallel fan-out and join

> Preserved verbatim from the original `README.md`. Describes commits `f00dee3` ("Add parallel
> fan-out and join to orchestrated sagas") and `35c039a` ("Restructure OrderSaga for parallel
> fan-out"). See [`../saga-dsl.md`](../saga-dsl.md) (`TransitionTo`/`Finalize` selector overloads) for
> the current reference documentation.

---

## Parallel fan-out and join

Closes the last engine item from the original v1 roadmap note: an orchestrated saga can now dispatch
several branches at once and wait for all of them, instead of being limited to one message per state.

**Half of it already worked.** `.Publish(...)` chains, so a single step could always dispatch several
commands — the fan-out needed no new DSL at all. What was missing was the *join*: replies come back in
an order nobody controls, and `TransitionTo(state)` was unconditional, so there was no way to express
"stay here until the last branch reports."

That is the same shape as the choreography join one pass earlier, so it got the same treatment — a
state-dependent overload rather than a new subsystem:

```csharp
During(Gathering)
    .When<StockReserved>()
        .Then((ctx, _) => ctx.Saga.StockReserved = true)
        .TransitionTo(s => s.AllBranchesReady ? ReadyToShip : Gathering)
    .When<PaymentAuthorized>()
        .Then((ctx, _) => ctx.Saga.PaymentAuthorized = true)
        .TransitionTo(s => s.AllBranchesReady ? ReadyToShip : Gathering);
```

Returning the gathering state keeps the saga waiting; returning the next state releases it. Register
the same selector on every branch and whichever reply lands last is the one that advances the saga,
with no branch assuming it is last. `StepDefinition.ResolveTargetState` is the single place the fixed
and computed forms are collapsed, so the orchestrated and choreographed DSLs can't drift — the same
reason `ResolveFinalStatus`, `StepExecutor`, and `CompensationRunner` are shared.

**One timeout covers the whole gather.** Returning the gathering state is a self-transition, which the
orchestrator treats as "no transition" and therefore neither cancels nor reschedules that state's
timeout. That is what a join wants — an arriving branch must not silently extend the deadline — but it
does mean a branch can't carry a separate deadline of its own.

**A correction to an earlier claim in this README.** The choreography pass said `Finalize(selector)`
was deliberately *not* added to the orchestrated `EventBuilder`, on the reasoning that orchestration
gates by state and so can express a conditional ending as separate `During(...)` branches. That holds
in general but not for a *terminal* join, where the last branch to arrive must both release the join
and finish the saga, and no branch knows it is last — a fixed `Finalize(status)` on each branch would
complete the saga on the first reply. `EventBuilder` now has the overload too, and the old note is
corrected in place.

**A silent engine limitation found while writing the tests, now loud.** Giving the second test saga the
same `TState` as the first made it vanish: `AddSaga<TDefinition, TState>` registers the definition as
`ISagaDefinition<TState>`, so a second saga sharing a state class silently wins the registration and
the first never runs — no error, its messages simply go nowhere. `AddSaga` now throws a
`SagaDefinitionException` at registration instead, because the runtime symptom (an inexplicably missing
saga) points nowhere near the cause. Each saga needs its own state class even if two would be
structurally identical.

**Coverage.** `ParallelFanOutJoinTests` pins the fan-out (one step, three outbound commands), the join
across all six arrival orders, the terminal-join variant across all six, that arriving branches don't
reset the gather timeout, and that a stalled gather still times out and compensates.
`SagaRegistrationTests` pins the duplicate-state guard. Verified by mutation rather than assumed:
making `ResolveTargetState` ignore its selector fails 13 tests, all of them in the fan-out suite and
nothing else.

**Now wired into the OrderProcessing sample.** This section originally left `OrderSaga` alone
deliberately — restructuring it is a product decision about what the sample is *for*, not a detail to
fold into the pass that built the primitive, and it doubles as this project's reference for the whole v1
slice, not just an example of the linear shape. Confirmed before touching it rather than defaulted into:
`OrderSaga` now merges its old `AwaitingInventory` and `AwaitingPayment` states into one `Gathering`
state that fans out `ReserveInventory` and `ChargePayment` together and joins on both replies — exactly
the "obvious demonstration" this section named above. The actual blast radius turned out narrow: no
engine or dashboard test is coupled to the sample's real state names (the closest lookalike,
`TestOrderSaga` in `VSaga.Core.Tests`, is a fully independent fixture with its own duplicate contract
types), so this is `dotnet/samples/VSaga.Samples.OrderProcessing/OrderSaga.cs` plus this section and the
"Timeout coverage for every awaiting state" note above pointing at it.

**A real behavioural change, not just a rename.** Charging the card no longer waits on inventory being
confirmed first — a real trade-off (favours latency over never charging for stock that turns out
unavailable), named here rather than left implicit. And because reserving and charging now run
concurrently, either failure can arrive while the *other* branch has already succeeded — impossible in
the old strictly-sequential shape, where payment was never attempted until inventory had already been
confirmed. Both `InventoryReservationFailed` and `PaymentFailed` therefore compensate now, not just the
payment one as before, and `Gathering`'s own compensation is unconditionally defensive — it always sends
both `ReleaseInventory` and `RefundPayment` rather than checking which branch actually landed, for the
same reason the timeout paths already had to be defensive.

**A real concurrency bug this pass found, not merely a hypothetical one.** The first version of
`Gathering`'s compensation sent its two messages via `Task.WhenAll`, on the reasoning that two
independent publishes should be able to run concurrently. Live chaos testing disagreed within minutes:
`ctx.PublishAsync` shares this saga's own context (and, transitively, the one EF Core `DbContext` behind
its event log) across every action in a step, and that is only ever safe one operation at a time — the
same reason every `.Publish(...).Publish(...)` chain elsewhere in this DSL runs sequentially. Concurrent
publishes threw `InvalidOperationException: A second operation was started on this context instance
before a previous operation completed` 22 times in one run, and — the actually damaging part — 13 of 20
compensation attempts in that window logged `CompensationStepFailed` rather than
`CompensationStepSucceeded`, meaning a real order's release/refund could silently only half-send. Fixed
by awaiting the two publishes sequentially instead; the re-run below shows zero exceptions and zero
compensation failures under the same chaos settings. Nothing about the `.Publish(...)` chains already
used everywhere else in this DSL was ever at risk — those were already sequential by construction — this
was specific to writing a multi-publish compensation delegate by hand with the wrong assumption about
concurrency safety.

**Live verification**, under `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d
--build` against the real stack, counts filtered to instances created after the `order-processing`
container's own restart (the Postgres volume is reused, not reset):

- First run (with the `Task.WhenAll` bug still in place), 32 fresh instances: 11 `Completed`, 8
  `Failed`/`Failed` (an explicit `...Failed` reply), 10 `Failed`/`TimedOut`, 3 still `Gathering`. 22
  `DbContext` concurrency exceptions logged, 13 of 20 compensation attempts recorded
  `CompensationStepFailed`.
- One instance from that run traced end to end shows exactly the new race this restructuring makes
  possible: `PaymentCharged` landed first (a real charge), then — about a second later —
  `InventoryReservationFailed` arrived (chaos-simulated out-of-stock). Compensation correctly fired both
  `ReleaseInventory` and `RefundPayment` — refunding a charge that had genuinely already gone through,
  a scenario the old sequential design could never reach from this branch, since payment was never
  attempted until inventory had already succeeded.
- A second traced instance shows the timeout half of the same story: `PaymentCharged` landed, but no
  inventory reply ever arrived (chaos-dropped); `Gathering`'s own 30s timeout fired
  (`TimeoutFired` ~31.5s after entering `Gathering`) and compensation again sent both messages
  defensively, correctly refunding the real charge alongside a release for a reservation that may or may
  not have happened.
- A traced happy-path instance confirms the join itself: `PaymentCharged` arrived first and only
  self-transitioned (no `ShipOrder` published yet, since `InventoryReserved` was still false);
  `InventoryReserved` arrived second, both flags true, `ShipOrder` published from *that* branch's own
  `Then`, and the saga proceeded to `AwaitingShipment` → `Completed` on `OrderShipped` exactly as before.
- After the `Task.WhenAll` fix, a fresh run (same chaos settings, new container start) of 32 fresh
  instances: 13 `Completed`, 7 `Failed`/`Failed`, 11 `Failed`/`TimedOut`, 1 still `Gathering`. **Zero**
  `DbContext` concurrency exceptions and **zero** `CompensationStepFailed` entries across the whole
  window — confirming the fix, not just assuming it from reading the diff.
- The mirror of the traced race above turned up in the same post-fix run: `PaymentFailed` arrived first
  (card declined, nothing charged), compensation correctly fired, and the saga finalized `Failed` — then,
  about a second later, a slow `InventoryReserved` reply landed against the now-terminal instance.
  Logged as `UnexpectedEvent` against `FromState=Failed`, exactly the existing "late reply after
  completion" handling this engine already has elsewhere, not a new failure mode this restructuring
  introduced — worth citing as confirmation, not as a gap.
- Zero unhandled exceptions or crash-level log lines from either container across either run, aside from
  the concurrency exception itself (caught internally by `HandleStepFailureAsync`'s own catch, not a
  crash — it just meant the compensation step recorded as failed rather than succeeded).

**Not unit-tested at the sample level**, matching this repo's established pattern for every other sample
saga (`InvoiceDeliverySaga`, `InvoiceFollowUpSaga`, `InvoiceArchivalSaga`): the underlying join primitive
is already tested by `ParallelFanOutJoinTests`/`TestParallelFulfilmentSaga`, so `OrderSaga`'s own
correctness is verified live rather than by a redundant unit-test double.
