# History: sub-saga composition Slice 2b, engine safety net (and Slice 3's closure)

> Preserved verbatim from the original `README.md`. Describes commits `12d2630` ("Build sub-saga
> composition Slice 2b: engine safety net") and `a846dba` ("Close sub-saga composition Slice 3: no
> automatic compensation cascade"). See [`../saga-dsl.md`](../saga-dsl.md) and
> [`../concepts.md`](../concepts.md) for the current reference documentation of `ChildSagaFinished`.

---

## Sub-saga composition: engine safety net

Slice 2b of [`docs/design/sub-saga-composition.md`](../design/sub-saga-composition.md), and the last of the three
slices that section originally scoped. `ctx.NotifyParentAsync` (Slice 2a) only works when a child's own
step code reaches a point where it can call it — two cases structurally cannot reach that point at all:
a child that fails via an unhandled exception, and a child that times out. The engine now covers both by
publishing `ChildSagaFinished` to the parent on the child's behalf:

```csharp
public sealed record ChildSagaFinished(Guid ChildCorrelationId, string ChildSagaType, SagaStatus Status);
```

Published directly by `SagaOrchestrator` — not through `ISagaContext`, unlike every other outbound
message in this engine — because this is the engine speaking on a child's behalf, not the child's own
step code. It fires from exactly two places: `HandleStepFailureAsync`'s exception path (a child's step
threw, so `HandleAsync` never returned a normal outcome) and `HandleTimeoutAsync`'s timeout path, but
only when the timeout goes terminal. It deliberately does **not** fire from the ordinary
message-driven success path, even when that path finalizes the saga — that's `NotifyParentAsync`'s
territory, and a child that reports its own result there needs no redundant, data-free duplicate from
the engine.

**Opt-in fell out of a mechanism that already existed, with no new DSL call.** The design sketch
considered a new per-definition call mirroring `OnUnhandledEvent(policy)`. Building it showed that was
unnecessary: `SagaRuntime<TState>.Subscription` is built from `ISagaDefinition.MessageTypes` — the union
of every message type a saga has declared a handler for, in any state. A parent that never declares
`.When<ChildSagaFinished>()` anywhere in its own DSL is therefore never even subscribed to the message
type, so the transport never delivers it — the same reason `InvoiceArchivalFinished` never reaches
`OrderSaga`/`PostShipmentChoreography` despite them sharing a correlation id with `InvoiceFollowUpSaga`
(see [`sub-saga-completion-notification.md`](sub-saga-completion-notification.md) above). Declaring the handler *is* the opt-in.

**A documentation correction, found while checking why opt-in mattered at all.** The design doc claimed
`UnhandledEventPolicy.Throw` makes an unhandled message "nack and redeliver forever." Reading
`SagaOrchestrator.RunStepAsync` shows that's not what happens: the exception an unhandled event throws
under `Throw` is caught by `RunStepAsync`'s own catch block and routed to `HandleStepFailureAsync` — the
same path a genuine step failure takes — which marks the saga `Failed` and **acks** the message. There is
no redelivery loop. The real hazard `Throw` poses to an un-opted-in parent is a silent, one-shot false
`Failed`, not an infinite spin — corrected in `docs/design/sub-saga-composition.md`. Either way, the conclusion
that (b) needs to be opt-in per parent stood; only the mechanism and the specific failure mode changed.

**A race, analogous to Slice 2a's, found the same way.** A child that fails via exception in the very
same step `StartChildAsync` started it in publishes `ChildSagaFinished` while still nested inside the
parent's own `StartChildAsync` call, under `InMemoryMessageTransport`'s synchronous/recursive dispatch —
before the parent has persisted its own transition. Same outcome as the `NotifyParentAsync` race:
`UnexpectedEvent`, silently dropped, no redelivery. Only the StepFailed path can race this way — a
timeout is dispatched independently by `SagaTimeoutDispatcherHostedService`'s own poll loop and can never
nest inside a `StartChildAsync` call. Pinned by
`ChildSagaFinishedTests.ChildSagaFinished_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition`,
not fixed, for the same reason Slice 2a's race wasn't: a fix means reordering this engine's "run step
actions, then persist" sequence throughout, well beyond this slice's scope.

**A dedicated timeline entry for the whole child-linkage story, not just the finish.** `SagaEntryType`
gained `ChildSagaStarted` (retagging `StartChildAsync`'s own publish, previously an ordinary
`MessagePublished` per Slice 1's deferred note) alongside `ChildSagaFinished`, both appended after
`MessageSent` per the append-only rule. Both are still, mechanically, outbound publishes, so
`SagaMapBuilder` treats them exactly like `MessagePublished`/`MessageSent` for edge-stitching — no
bespoke map logic needed. The Angular timeline/map views render `entryType` as plain text with no
per-type icon switch, so the frontend change was two string literals in `saga.model.ts`.

**In the sample.** `InvoiceFollowUpSaga` opts in with a `.When<ChildSagaFinished>()` branch on
`AwaitingArchival`, alongside its existing `.When<InvoiceArchivalFinished>()` branch:

```csharp
.When<ChildSagaFinished>()
    .Then((ctx, _) => ctx.Saga.InvoiceArchived = false)
    .TransitionTo(Abandoned)
    .Finalize(SagaStatus.TimedOut);
```

`InvoiceArchivalSaga`'s own timeout branch is unchanged — it still never calls `NotifyParentAsync`, by
design (see its doc comment). Before this slice, that meant `InvoiceFollowUpSaga` could only learn about
a timed-out child via its own independent 30s timeout. Now the engine's safety net reaches it in ~15s,
as soon as `InvoiceArchivalSaga`'s own `StorageTimeout` fires — the parent's 30s timeout becomes a true
backstop rather than the only rescue.

**Live verification**, under `docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d
--build` (chaos on, so `InvoiceArchivalSaga`'s `StoreInvoiceCopy` timeout actually has a chance to fire):

- A ~8-minute run against a freshly-built stack (excluding sagas from the volume's pre-existing history,
  which predate this pass's code — see below) produced 20 `InvoiceFollowUpSaga` instances: 12
  `Archived`/`Completed`, 7 `Abandoned`/`TimedOut`, 1 still `Running`.
- **Of the 7 timed-out parents, the split is exactly what the design predicts**: 4 were rescued by the
  engine safety net in 17.4–23.3s (`MessageReceived ChildSagaFinished` on the parent's own timeline, no
  `TimeoutFired` at all), and 3 hit the parent's own 30s backstop timeout at 31.4–34.1s (`TimeoutFired`,
  no `ChildSagaFinished` ever arrived) — the true-backstop behaviour the design intends when chaos also
  drops the safety-net publish itself, or the notification simply loses the race to nothing.
- **One pair traced end to end.** Child `InvoiceArchivalSaga` (created 17:11:02.347): `SagaStarted
  ArchiveInvoice` → `MessagePublished StoreInvoiceCopy` → `TimeoutScheduled AwaitingStorage` →
  `TimeoutFired` (15s later) → `StepSucceeded AwaitingStorage → Failed` → **`ChildSagaFinished`** — the
  new dedicated entry type, not `MessagePublished`. Its parent `InvoiceFollowUpSaga` shows
  `MessageReceived ChildSagaFinished` 8ms after that, `StepSucceeded AwaitingArchival → Abandoned`,
  `SagaCompleted` — 17.4s total from the parent's own `SagaStarted`, well under its 30s
  `ArchivalWaitTimeout`.
- **The scope boundary held live, not just in tests.** `InvoiceArchivalSaga` also produced 2 fresh
  `Failed`/`Failed` instances via its ordinary business-failure path (`InvoiceCopyStorageFailed` →
  `NotifyParentAsync` → `Finalize(Failed)`) — traced end to end, neither one logged a `ChildSagaFinished`
  entry alongside its `MessagePublished InvoiceArchivalFinished`, confirming the engine safety net stays
  silent on the ordinary success path exactly as designed.
- **Linkage integrity, same check as Slices 1 and 2a**: 26 fresh `InvoiceArchivalSaga` children, zero
  half-linked rows, zero without a parent.
- **The fan-out stayed narrow, confirmed live**: pulling `OrderSaga`'s and `PostShipmentChoreography`'s
  own timelines for a rescued order (all three share the correlation id) shows neither ever received
  `ChildSagaFinished` — neither declares a handler for it, so neither is even subscribed.
- Zero unhandled exceptions or crash-level log lines in either container across the whole run.
- **Not observed live**: the `HandleStepFailureAsync`/unhandled-exception path. Nothing in the
  `OrderProcessing` sample throws an unhandled exception from a step, so only the timeout path actually
  exercised the engine safety net under real chaos timing — an honest gap, covered instead by
  `ChildSagaFinishedTests`' real publish→receive→orchestrator path and the mutation pass above, the same
  way this repo has treated every other live-verification gap.
- **Incidental finding, not a defect**: the compose run reused an existing Postgres volume rather than a
  fresh one. One pre-existing `InvoiceFollowUpSaga` instance (created ~40 minutes before this run's
  containers started) shows its child-start hop as the old `MessagePublished` rather than
  `ChildSagaStarted`, because it was created by the previous image before this pass existed — a live,
  incidental demonstration of "additive, upgrades in place" working as intended, the same property the
  migrations elsewhere in this README rely on. Excluded from the counts above, which only cover instances
  created after this run's containers started.

**Verified by mutation, four ways**, same discipline as Slices 1 and 2a. Publishing under the child's
own correlation id instead of the parent's fails exactly the 4 tests that depend on real delivery to the
parent, and nothing else. Removing the root-saga guard (falling back to the child's own id instead of
skipping) fails exactly the one test that pins a root saga never publishing. Making the ordinary
message-driven success path also publish `ChildSagaFinished` fails exactly the one scope-boundary test —
confirmed clean across the rest of the solution's test suite (all 201 tests across every project), not
just this file, since no other registered parent in this repo both declares a `ChildSagaFinished`
handler and has a child that finishes ordinarily. Dropping `StartChildAsync`'s `ChildSagaStarted`
entry-type override back to the old `MessagePublished` fails exactly the one Slice-1-era test updated
for this slice.
