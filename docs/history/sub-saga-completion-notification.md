# History: sub-saga composition Slice 2a, completion notification

> Preserved verbatim from the original `README.md`. Describes commit `d7a7cb9` ("Build sub-saga
> composition Slice 2a: completion notification"), following the design analysis added in `27d027e`
> (`docs/design/sub-saga-composition.md`). See [`../saga-dsl.md`](../saga-dsl.md)
> (`ISagaContext.NotifyParentAsync`) for the current reference documentation.

---

## Sub-saga composition: completion notification

Slice 2a of [`docs/design/sub-saga-composition.md`](../design/sub-saga-composition.md). A child can now address its
parent directly, and a parent can actually wait for the answer rather than only parking until its own
timeout:

```csharp
.Then((ctx, m) => ctx.NotifyParentAsync(new InvoiceArchivalFinished(ctx.Saga.OrderId!, Archived: true), ctx.CancellationToken))
```

`NotifyParentAsync` publishes under `Saga.ParentCorrelationId` — the same field `StartChildAsync`
stamped onto this instance when the engine created it — so a parent parked in a `During(state)` waiting
for a specific message type sees it arrive as an ordinary inbound message. Not a general
publish-under-any-id overload: the only id this can address is the one the engine already put on this
saga's own state, so nothing can mint an orphan instance under an id it invented. It throws immediately,
before any I/O, if this saga has no parent.

**Why this needed an engine change at all**, corrected from the original design sketch: `PublishAsync`
always stamps the *publishing* saga's own correlation id, and the orchestrator correlates strictly on the
inbound id, so a child's `PublishAsync("I'm done")` was never reaching its parent — it addressed the
child's own instance. `CorrelateBy` doesn't rescue this either: paired with `CorrelateOn`, it now drives
a same-saga-type business-key lookup when the transport correlation id misses, but that lookup is
scoped to `(SagaType, BusinessKey)` and can never cross into a different saga type — exactly what a
child addressing its parent needs. `NotifyParentAsync` is the missing piece: the only new capability is
publishing under a correlation id this saga did not itself open.

**Two options were on the table** (`docs/design/sub-saga-composition.md` §3.4) — a child publishing its own
result, or an engine-published `ChildSagaFinished(status)` — and working through them turned up that
they're complementary, not alternatives: only a child's own message can carry its actual result (what
was charged, whether the invoice archived), and only an engine-published event could ever fire when a
child fails via an unhandled exception or simply times out, since neither reaches the child's own
publish step. This pass builds the first half. The second — an opt-in safety net for the cases this
one structurally cannot reach — remains proposed as Slice 2b.

**In the sample: a new pair, not a retrofit onto `PostShipmentChoreography`.** `InvoiceFollowUpSaga`
starts an `InvoiceArchivalSaga` per `InvoiceIssued` and waits for it — the first live parent in this
repo that parks and is released early rather than only by timeout. It is deliberately *not*
`PostShipmentChoreography` gaining a wait: that saga's own doc comment is explicit that its leg must
complete once all three post-shipment services have reported, and an undeliverable invoice must not
hold it open. Waiting needs a state that parks (`During(state).When<T>().TransitionTo(...)`), which is
the orchestrated DSL's shape, not the choreography's `On<T>().Finalize(Func)` join — retrofitting a park
onto it would have meant either blocking its completion on archival, contradicting its own documented
invariant, or reacting to a message after the instance is already terminal, neither of which
demonstrates a real wait. It's also a new *child*, `InvoiceArchivalSaga`, rather than a second observer
of `InvoiceDeliverySaga`: reusing that one would file two customer emails per invoice instead of one
email and one accounting copy — a different, real concern, with its own real failure mode (the archive
store being unavailable). `InvoiceFollowUpSaga` itself shares the order's correlation id, the same as
`PostShipmentChoreography` — both react to `InvoiceIssued`, so both simply open under whatever
correlation id that message already carries, no special wiring required.

**The child's own timeout is deliberately not covered.** `InvoiceArchivalSaga` calls
`NotifyParentAsync` from both terminal steps that a real reply reaches — `InvoiceCopyStored` and
`InvoiceCopyStorageFailed` — but not from its own `WithTimeout`. A timed-out child never reaches any
step that could call it, which is exactly the structural gap Slice 2b exists to cover; leaving it
uncovered here is what makes `InvoiceFollowUpSaga`'s own, longer timeout (30s, against the child's 15s)
do real work rather than being redundant. Verified live below.

**A race this pass found, not one it went looking for.** A child that calls `NotifyParentAsync` from
the very same step that `StartChildAsync` started it in can race ahead of the parent's own
not-yet-persisted transition. `InMemoryMessageTransport.DispatchAsync` invokes every subscriber
synchronously and recursively, so such a notification is still nested inside the parent's own
`StartChildAsync` call when it arrives — before the parent has persisted its state, or, for a brand-new
parent, inserted a row at all. `SagaOrchestrator.HandleCoreAsync` finds no existing instance, decides
the message isn't among the parent's initiating types, and logs `UnexpectedEvent` — no exception, so no
redelivery either, and the notification is silently gone. Pinned by
`NotifyParentAsync_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`
(`dotnet/tests/VSaga.Core.Tests/NotifyParentAsyncTests.cs`) rather than quietly avoided. Not fixed: a real fix
would mean reordering this engine's "run step actions, then persist" sequence throughout, well beyond
this pass. Real transports decouple a child's dispatch from the publisher's call stack rather than
nesting it, and every real child in this repo has genuine I/O between the two calls (a participant
round-trip), so this did not reproduce once under 21 real archival children live — but a child that
reports back with no intervening work at all remains a real, narrow hazard, worth knowing rather than
assuming away.

**Live verification**, under `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d`
(chaos on, so the child's own timeout actually has a chance to fire — see the "Chaos-engineering
transport middleware" section):

- 21 `InvoiceArchivalSaga` children, 23 `InvoiceFollowUpSaga` parents, zero dangling parent pointers and
  zero half-linked rows — the same shape of check Slice 1's linkage got, now over a notification path
  running the opposite direction.
- All three endings fired: 19 `Archived`/`Completed`, 1 `Failed`/`Failed` (a real archive-store
  failure), 1 `Failed`/`TimedOut`.
- The timed-out pair traced end to end: the child's own timeline shows `TimeoutFired` →
  `StepSucceeded AwaitingStorage → Failed`, with no `NotifyParentAsync` publish anywhere on it. Its
  parent reached `Abandoned`/`TimedOut` roughly 35s after creation — its own 30s timeout, not the
  child's 15s one, and not a notification — which is live confirmation that the child's timeout does
  not notify and the parent's independent timeout is genuinely what rescues it.
- A completed pair traced end to end: `SagaStarted InvoiceIssued` → `MessagePublished ArchiveInvoice` →
  `TimeoutScheduled` → `MessageReceived InvoiceArchivalFinished` → `StepSucceeded AwaitingArchival →
  Archived` → `SagaCompleted`, entirely on the parent's own timeline.
- **The fan-out note from §3.4 turned out narrower than written there.** `InvoiceFollowUpSaga` shares
  its correlation id with `OrderSaga` and `PostShipmentChoreography`, so in principle
  `InvoiceArchivalFinished` "fans out to every saga type tracking that id." In practice, subscription is
  per explicitly-declared message type, not per correlation id — confirmed by pulling `OrderSaga`'s own
  timeline for an order that went through this path: no `InvoiceArchivalFinished` entry, because
  `OrderSaga` never declares a handler for it and so never even subscribes.

**Verified by mutation, twice**, same discipline as Slice 1. Publishing under the child's own
correlation id instead of `Saga.ParentCorrelationId` fails exactly the 3 tests in
`NotifyParentAsyncTests` that depend on a real notification reaching its parent, and nothing else.
Treating the read of `Saga.ParentCorrelationId` as always absent fails the same 3.
