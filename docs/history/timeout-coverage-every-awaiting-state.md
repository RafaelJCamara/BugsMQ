# History: timeout coverage for every awaiting state

> Preserved verbatim from the original `README.md`. Describes commit `a28990d` ("Give every awaiting
> state a timeout, and fix a dead one in the choreography").

---

## Timeout coverage for every awaiting state

Reverses a choice this README had defended across two passes: `AwaitingInventory` and
`AwaitingShipment` deliberately had no `WithTimeout`, documented as an honest gap chaos testing had
surfaced rather than something to quietly patch. The note ended "revisit only if that framing should
change." It changed, for a reason the data made hard to argue with: at the time of this pass the
database held **60 sagas stuck `Running` forever** — 43 in `AwaitingInventory`, 17 in
`AwaitingShipment` — each one an order holding inventory, and in the shipment case a charged card, with
no path back. "Documented gap" stops being the honest framing once it has a body count.

All three awaiting states now share one `ReplyTimeout` (30s) and one recovery shape,
`Compensate().TransitionTo(Failed).Finalize(TimedOut)`. They differ only in how much they unwind,
because `Compensate()` walks the states the instance actually visited, most-recent first:

| Timed-out state | Unwinds |
| --- | --- |
| `AwaitingInventory` | releases the hold `ReserveInventory` may or may not have taken |
| `AwaitingPayment` | releases the hold and defensively refunds (unchanged behaviour) |
| `AwaitingShipment` | refunds and releases — identical to the `ShipmentFailed` branch |

**`AwaitingInventory` and `AwaitingPayment` were later merged into one `Gathering` state** by the
[`parallel-fan-out-and-join.md`](parallel-fan-out-and-join.md) pass below — this table describes the shape at the time this pass shipped,
and is retained as-is rather than rewritten; the unwind behaviour it describes didn't go away, it moved
onto the merged state's own compensation. The 60 stranded sagas this section already knew about were
backfilled against the pre-merge names (see "Backfilling the 60 stranded sagas" immediately below) —
resolved before the merge could affect them, not because of anything this table needed to change.

**The constraint this places on participants is the interesting part.** A timeout always means "no
reply arrived in time", never "the participant declined" — a decline is a real reply and takes the
explicit `...Failed` branch instead. So compensation here is necessarily *defensive*: the request may
well have succeeded with only its reply lost, which means every compensating message has to be safe to
receive for work that never happened. That is a real design constraint the sample now depends on, not
an incidental detail.

**A live defect this pass found in the choreography shipped one pass earlier.** Checking
`TimeoutScheduled` rows per state against the running stack showed every `PostShipmentChoreography`
milestone with hundreds of scheduled timeouts — except its initial state, which had **zero**. The cause
is an engine rule that fails silently: `SagaOrchestrator` schedules a state's timeout only on a real
transition (`ToState != FromState`), and `OrderShipped` recorded the initial state, so the
instance-creating event was a self-transition that scheduled nothing. An order whose three
post-shipment events were all lost would have hung forever, and the previous README section claimed
that case was covered. Fixed by giving the opening event its own `Shipped` milestone so the transition
is real; `AwaitingFulfilment` is now deliberately excluded from the timeout list, since nothing ever
transitions into an initial state. `InitialStateTimeoutTests` pins both halves of the rule so it can't
regress silently elsewhere.

**Live verification** under the chaos overlay, comparing before and after on the same database:

- `TimeoutScheduled` gained `AwaitingShipment` (0 → 22) and `Shipped` (0 → 15, the choreography fix);
  `AwaitingInventory` went 2 → 46.
- `TimeoutFired` gained `AwaitingInventory` (0 → 8). Before this pass, `AwaitingPayment` was the only
  state that had ever fired a timeout in this database's entire history — 191 of them.
- One `AwaitingInventory` timeout traced end to end through its event log: `ReserveInventory`
  published, reply dropped by chaos, `TimeoutScheduled` → `TimeoutFired` → `CompensationStarted` →
  `ReleaseInventory` published → `CompensationStepSucceeded` → `Failed`/`TimedOut`. InventoryService's
  own logs confirm it received the release, so the compensation reached a real participant rather than
  just being recorded.
- **Not directly observed:** an `AwaitingShipment` timeout firing. Twenty-two were scheduled, but no
  shipment reply happened to be dropped inside the observation window. The code path is the one
  `AwaitingInventory` exercised, and the multi-compensation unwind is the one `AwaitingPayment` has
  fired 197 times, so both halves are covered in combination — but stated plainly rather than implied.

**What this does not do:** it does not rescue the 60 already-stuck sagas. Timeouts are scheduled when a
saga transitions *into* a state, so instances that entered `AwaitingInventory`/`AwaitingShipment` before
this change have no timeout row and never will. They stay stuck. Draining them would need a separate
backfill — deliberately not smuggled into this pass.
