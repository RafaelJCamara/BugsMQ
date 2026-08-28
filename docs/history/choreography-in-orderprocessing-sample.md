# History: choreography in the OrderProcessing sample

> Preserved verbatim from the original `README.md`. Describes commit `a87f40e` ("Add a choreographed
> saga to the OrderProcessing sample").

---

## Choreography in the OrderProcessing sample

Closes the last item the two preceding sections left open. The sample now runs both saga kinds side by
side in one process, against one database, under one correlation id per order.

**The process.** Once `ShippingService` publishes `OrderShipped`, three further services —
`NotificationService`, `LoyaltyService`, `InvoicingService` — each react on their own initiative and
announce what they did (`CustomerNotified`, `LoyaltyPointsAwarded`, `InvoiceIssued`). Nothing commands
them: the new contracts are all events, with no matching `Do-X` command, which is the structural
difference from every other leg of this sample. `PostShipmentChoreography` observes that fan-out and
decides when the leg is finished. It commands none of the three.

> **Amended by a later pass.** "It publishes nothing" stopped being true when
> `PostShipmentChoreography` gained a `StartChildAsync` on its `InvoiceIssued` branch — see
> [`sub-saga-parent-linkage.md`](sub-saga-parent-linkage.md) below. It still commands none of the three
> fan-out services and still waits on nothing it started; the join described here is unchanged.

**It shares `OrderSaga`'s correlation id**, because it is the same business transaction. The three
participants propagate the inbound correlation id onto their replies via `MessageEnvelope.From`, so
their events land on the tracker without anyone minting a new id. Both sagas therefore appear together
under `GET /api/correlations/{id}`. Two things had to already be true for this to work, both of them
from the identity pass above: the composite `(SagaType, CorrelationId)` key, and per-saga-type dedupe —
`OrderShipped` is delivered to `OrderSaga` and to `PostShipmentChoreography` alike (one queue per
subscription bound to a topic exchange), and under the old correlation-id-only `IsDuplicateAsync` the
second saga to see it would have discarded its own only copy.

**This surfaced a real gap in the choreography DSL, which is the interesting part.** A fan-out/join is
*the* characteristic choreography shape, and `Finalize(SagaStatus)` could not express its ending. With a
fixed status the only options were to nominate one branch as the finisher — wrong, because three
independent publishers have no fixed order and the nominated one may well land first — or never to
complete at all. So `ChoreographyEventBuilder` gained an overload:

```csharp
.Finalize(state => state.CustomerNotified && state.PointsAwarded && state.InvoiceIssued
    ? SagaStatus.Completed
    : null)   // null = handled, but not terminal yet
```

Registered identically on all three branches, this makes whichever branch arrives last the one that
completes the saga, without any branch assuming it is last. It is evaluated *after* the step's own
actions, so the branch that sets the final flag sees it. `StepDefinition.ResolveFinalStatus` is the one
place the fixed and conditional forms are collapsed, so the orchestrated and choreographed DSLs cannot
drift on which wins — the same reason `StepExecutor` and `CompensationRunner` are shared. The overload
is deliberately *not* added to the orchestrated `EventBuilder`: an orchestrated saga gates steps by
current state, so a conditional ending is already expressible there as separate `During(...)` branches.

`ChoreographyFanOutJoinTests` pins this across all six arrival orders. Verified by mutation rather than
assumed: replacing the selector with a fixed `Finalize(SagaStatus.Completed)` on one nominated branch
fails exactly the four orders in which that branch is not last, and passes the two in which it is.

**A subtlety the sample documents in place:** every non-terminal milestone registers its own timeout,
not just the first. Timeouts are keyed on `CurrentState` and the orchestrator cancels the pending one
whenever the saga transitions away, so a single `WithTimeout(AwaitingFulfilment, ...)` would be silently
cancelled by the first branch to report and could only ever catch an order where *nothing* came back —
leaving a saga stalled at two-of-three to hang forever.

**Not unit-tested at the sample level, by existing convention:** `dotnet/tests/` holds one project per `dotnet/src/`
project and none for `dotnet/samples/`, so the sample's own wiring is verified live rather than in xUnit. What
*is* unit-tested is the engine capability behind it, via a reduced fixture (`TestFanOutChoreography`)
with the same shape.

**Live verification** against `docker compose up`: both types appear in `/api/saga-types`
(`OrderSaga` Orchestrated, `PostShipmentChoreography` Choreographed); the three services' events
genuinely interleave across orders (loyalty-first, invoice-first and notify-first all observed in one
run); every choreography instance reached `Completed`, and their terminal `CurrentState` varied between
`Invoiced` and `PointsAwarded` — direct evidence that *different branches finished last* and the join
handled each. For a single order, `/api/correlations/{id}` returned both sagas, their timelines stayed
separate (14 vs 13 entries, neither containing an entry belonging to the other), and `OrderShipped`
appeared as an inbound message in both.
