# History: sub-saga composition Slice 1, parent linkage

> Preserved verbatim from the original `README.md`. Describes commit `b6600b8` ("Build sub-saga
> composition Slice 1: parent linkage"), following the design sketch added in `e53db67`
> (`docs/design/sub-saga-composition.md`). See [`../saga-dsl.md`](../saga-dsl.md)
> (`ISagaContext.StartChildAsync`) and [`../concepts.md`](../concepts.md) for the current reference
> documentation.

---

## Sub-saga composition: parent linkage

The first of the three slices in [`docs/design/sub-saga-composition.md`](../design/sub-saga-composition.md). A saga
can start another saga as a step, and the child records which instance started it:

```csharp
.Then((ctx, m) => ctx.StartChildAsync(new DeliverInvoice(m.OrderId, m.InvoiceNumber), ctx.CancellationToken))
```

`StartChildAsync` publishes that message under a **fresh** correlation id with two new envelope headers
(`x-vsaga-parent-saga-type`, `x-vsaga-parent-correlation-id`). Whichever saga's `CanInitiate` matches
becomes the child, and `SagaOrchestrator` reads those headers exactly once — when it creates the
instance — onto `SagaState.ParentSagaType`/`ParentCorrelationId`. Neither saga type references the
other; the whole link is two strings on the wire.

**A fresh id, not the parent's.** Sharing would have been simpler, and is wrong: the snapshot primary
key is `(SagaType, CorrelationId)`, so a shared id caps a parent at one child *per saga type*, and a
self-recursive saga collides with itself outright. This is also what distinguishes the relation from
the one in "Saga identity: (SagaType, CorrelationId)" above — `PostShipmentChoreography` shares
`OrderSaga`'s id because they are one transaction observed twice, whereas a child is a separate unit of
work under its own id. The dashboard shows them as two different strips, and
`/api/correlations/{id}` deliberately does not return children.

**Real columns, not just the state blob.** `ParentSagaType`/`ParentCorrelationId` ride along inside
`DataJson` for free, but `ISagaSummaryReader` is saga-type-agnostic and queries columns, so answering
"which sagas did this one start?" needs them projected out. Hence the `AddSagaParentLinkage`
migration (two nullable columns plus an index on the pair), `SagaSummary` carrying the pointer, and
`FindChildrenAsync` behind `GET /api/sagas/{sagaType}/{correlationId}/children`. Root sagas leave both
null, so on a workload with no sub-sagas that index stays effectively empty.

**What this deliberately does not do: waiting.** `StartChildAsync` returns as soon as the publish does.
The design sketch expected the parent's wait to need no new engine work — park in a state, let the
child's own message release it via the existing join primitive — and the *parent* half of that is
indeed already there. The **child** half is not, and this is the one claim in that document that
building it disproved: a child cannot address its parent at all today. `ctx.PublishAsync` always
stamps the publishing saga's own correlation id, and the orchestrator correlates strictly on the
inbound correlation id, so a child publishing "I'm done" sends it under the child's id, where the
parent will never see it. `CorrelateBy` does not rescue this either: paired with `CorrelateOn`, it now
drives a business-key lookup when the transport correlation id misses (production-readiness.md
§5.2/§5.3) — but that lookup is scoped to `(SagaType, BusinessKey)`, so it can only ever find another
instance of the *same* saga type. A child and its parent are different saga types by construction, so
this still can't address the parent. So "child publishes its own domain message", recorded there as
working today with no engine change, actually needs one: a publish overload that takes a target
correlation id. That changes the trade-off against an engine-published `ChildSagaFinished`, and the
decision is still open in the doc rather than quietly settled here.

**Compensation does not cascade into children — a closed decision, not a gap.** A parent's
`.Compensate()` only ever runs the parent's own registered compensation delegates; it never walks into
`FindChildrenAsync` or touches a child automatically. Considered and closed as "analysed, deliberately
not built" in [`docs/design/sub-saga-composition.md`](../design/sub-saga-composition.md) §3.5 (Slice 3): the parent
has no compile-time link to its children (`StartChildAsync` returns `Task`, not a child id), the child
tree is unbounded in depth since a child gets a fresh correlation id specifically so a saga can start
its own type, and — checked directly against this code before closing — compensation delegates run
against `ISagaContext<TState>`, which has no children-lookup method at all; only
`ISagaSummaryReader.FindChildrenAsync` does, and that is a read-model query compensation logic has no
route to. A parent that needs a child compensated publishes its own compensating command explicitly,
the same way it would address any other collaborator.

(A started child *is* distinguishable on the parent's own timeline, via the dedicated `ChildSagaStarted`
entry type — see [`sub-saga-engine-safety-net.md`](sub-saga-engine-safety-net.md) below for that and its `ChildSagaFinished`
counterpart.)

**Failure modes, documented rather than discovered.** `StartChildAsync` is a publish, so if no saga
initiates on that message type, no child is created and nobody is told — the parent transitions and
parks exactly as it would have on success, and only its own timeout eventually notices.
`AChildMessageNobodyInitiatesOn_StartsNothingAndTellsNobody` pins that as tested behaviour rather than
a surprise. Symmetrically, two saga types initiating on one child message would start two children with
the parent none the wiser. A half-stamped link (one header present, the other missing or unparseable)
is recorded as a root rather than a child, since `FindChildrenAsync` matches on the pair and a
one-sided link would show in the dashboard while being unreachable from the parent.

**In the sample.** `PostShipmentChoreography` now starts an `InvoiceDeliverySaga` per `InvoiceIssued`.
Issuing an invoice is not the same as the customer receiving it, and getting it delivered has its own
retry window (20s, deliberately different from `OrderSaga`'s 30s — not inheriting the parent's
deadlines is half the reason to make it a separate saga) and its own failure ending. `OrderSaga` is
untouched: this pass adds no state, timeout, or compensation to it, and the question of restructuring
it to demonstrate composition is left open in the design doc, exactly as the parallel fan-out pass left
it. `NotificationParticipant` grew a `SendInvoiceEmail` handler and neither knows nor cares that the
correlation id it is replying to belongs to a child saga.

**Live verification**, since the linkage rides on envelope headers and this repo has previously shipped
header threading the orchestrator never read (`SourceService`/`CausationId`, whose tests hand-built the
field and so proved nothing). Under `docker compose up` against real Postgres and RabbitMQ:

- The redeploy is a clean cutover in the data, which is the strongest available evidence that the
  linkage came from this code and not from somewhere else: of the `PostShipmentChoreography` instances
  that reached `InvoiceIssued`, every one created before the restart has zero children and **every one
  created after has exactly one** — 39/39, none with zero, none with two.
- 43 child instances, all with both columns populated. Zero half-linked rows
  (`ParentSagaType IS NULL <> ParentCorrelationId IS NULL`), and zero dangling pointers — every child's
  parent pair resolves to a real row.
- All three of the child's endings actually fired: `Delivered`/`Completed` 34,
  `Undeliverable`/`Failed` 8 (simulated bounce), `Undeliverable`/`TimedOut` 1 — the last under the
  chaos overlay, which dropped a `SendInvoiceEmail` and left that child to its own 20s timeout.
- One completed child traced end to end through its own timeline: `SagaStarted DeliverInvoice` →
  `MessagePublished SendInvoiceEmail` → `TimeoutScheduled AwaitingDelivery` → `MessageReceived
  InvoiceEmailSent` → `Delivered`/`SagaCompleted`. The timed-out one shows the same opening, then
  `TimeoutFired` → `Undeliverable`. Both entirely under the child's own correlation id.
- **A failed child does not touch its parent**, which is the property "does not wait" is supposed to
  buy: across all 43 pairs, every parent is `Completed` regardless of whether its child completed,
  bounced, or timed out.
- `curl .../children` returns the child with its parent pointer, and `curl` on the child returns a
  summary pointing back — while `/api/correlations/{parentId}` returns only `OrderSaga` and
  `PostShipmentChoreography`, correctly excluding the child.
- **Not verified in a browser:** the "started by" / "started" strips are covered by component tests
  and by the endpoint they read from, but the rendered page was not opened during this pass.

**Verified by mutation, twice**, once per end of the wire. Making `SagaOrchestrator` read a header name
nothing stamps fails 5 tests, all in `SubSagaCompositionTests` and nothing else; making `SagaContext`
publish the child's message without the linkage headers fails the same 5. The interesting half is what
*doesn't* fail: the EF Core provider tests and the dashboard endpoint tests stay green under both
mutations, because they set the parent pointer by hand. That is correct for them — their subject is the
store and the route — but it is also an exact reproduction, under lab conditions, of how the
`CausationId` tests passed for months against a header nobody read. The tests that can actually catch
it are the ones that never touch the linkage themselves and let `StartChildAsync` → transport →
orchestrator set it.
