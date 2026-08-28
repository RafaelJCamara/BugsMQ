# History: saga identity, (SagaType, CorrelationId)

> Preserved verbatim from the original `README.md`. Describes commit `303a3fe` ("Scope saga identity
> to (SagaType, CorrelationId)"). See [`../concepts.md`](../concepts.md) for the current reference
> documentation of correlation and saga identity.

---

## Saga identity: (SagaType, CorrelationId)

Closes the limitation the choreographed-saga pass documented directly above. A saga instance is now
identified by the pair `(SagaType, CorrelationId)` rather than by a correlation id alone, so two saga
types may track the same business transaction — which is precisely what lets a choreographed saga
observe a flow an orchestrated saga is already running.

**This is a breaking change to the public store contracts and the dashboard's URLs.** There is no
compatibility shim: this is a pre-1.0 library, and quietly keeping a "look up by correlation id alone"
path would have preserved exactly the ambiguity the change exists to remove.

**What moved.** `SagaInstances`' primary key became `(SagaType, CorrelationId)`, and every per-instance
read grew a leading `sagaType` parameter: `ISagaSnapshotStore<TState>.FindAsync`,
`ISagaEventLogStore.GetTimelineAsync`/`IsDuplicateAsync`, `ISagaTimeoutStore.CancelAsync`,
`ISagaAdminStore.ResetStateAsync`, `ISagaSummaryReader.GetAsync`/`GetDataJsonAsync`, and
`ISagaChangeNotifier.TimelineEntryAddedAsync`. `InsertAsync`/`UpdateAsync`/`AppendAsync` were left
alone — they already receive a `SagaState`/`SagaLogEntry` carrying its own `SagaType`.
`ISagaTimeoutStore.ScheduleAsync` had its first two parameters swapped purely for consistency, since it
was the one member that already took both and took them in the other order. The saga exceptions all
carry the saga type now, so their messages name an instance unambiguously. `SagaMapBuilder` needed no
change at all: it was already a pure function of a `SagaSummary` plus a pre-fetched timeline.

**Three of these were live correctness bugs, not just a missing feature.** Had a second saga type ever
started sharing a correlation id under the old code, then beyond the obvious
`SagaAlreadyExistsException` on insert:

- **Compensation would have run for states the saga never visited.**
  `SagaOrchestrator.GetVisitedStatesAsync` derives the compensation set from
  `GetTimelineAsync(correlationId)`. An unscoped timeline merges both sagas' entries, so one saga's
  `VisitedStates` would include the other's states — and `Compensate(state, ...)` is keyed on exactly
  those strings.
- **A broadcast message would have been silently swallowed.** `IsDuplicateAsync(correlationId,
  messageId)` is the idempotency check. The same message legitimately reaches several saga types; the
  second one to process it would have discarded its own *first* delivery as a duplicate.
- **One saga would have cancelled another's timeout.** State names are unique only within a saga type,
  so an unscoped `CancelAsync(correlationId, forState)` reaches across into a same-named state
  belonging to a different saga.

Each of these is now pinned by a test that fails against the unscoped query — verified by mutation,
not assumed: reverting the `SagaType` predicate in `EfCoreSagaEventLogStore` and
`EfCoreSagaTimeoutStore` fails exactly `TimelineAndDuplicateCheck_AreScopedToOneSagaType` and
`CancelTimeout_DoesNotCancelAnotherSagaTypesSameNamedState` and nothing else.

**Dashboard URLs.** Every per-instance route gained a saga-type segment:
`GET|POST /api/sagas/{sagaType}/{correlationId}[/timeline|/map|/retry]`. The list route
(`GET /api/sagas`) and `GET /api/saga-types` are unchanged. The Angular route became
`/sagas/:sagaType/:id`. SignalR's per-saga group is now `saga:{sagaType}:{correlationId}`, and
`TimelineEntryAdded` carries the saga type as a leading argument — without that, a detail view open on
one saga would receive the other's timeline entries.

A new `GET /api/correlations/{correlationId}` returns *every* saga instance tracking a correlation id.
That is the one place a bare correlation id is still a legitimate input: it's how a caller holding only
an id (an old bookmark, a log line, a support ticket) resolves it to a concrete instance. It returns a
list rather than a single summary precisely because the answer can now be more than one. It's mounted
at its own top-level path rather than `/api/sagas/by-correlation/{id}`, which would have sat in the same
route slot as `{sagaType}` and relied on literal-beats-parameter precedence to disambiguate.

> **Now surfaced in the dashboard** — the saga detail page resolves its correlation id through this
> endpoint, drops its own instance, and renders the rest as an "Also tracking this correlation id" strip
> linking to each sibling. Nothing renders in the ordinary one-saga case. Deliberately a snapshot rather
> than live: the detail page joins only its own instance's hub group, so a sibling's status change isn't
> pushed to it; the strip is refreshed whenever this saga itself updates, the same compromise the map tab
> already makes. Added in the pass that shipped the sample choreography, which is what first made a
> second saga per correlation id something you could actually click through to.

**Migration.** `20260825045219_ScopeSagaIdentityToSagaTypeAndCorrelationId` swaps the primary key and
re-leads the two `SagaEventLog` indexes with `SagaType`. No data migration is needed: `SagaType` was
already non-null on every row, and correlation ids were globally unique under the old key, so no
existing row can collide under the new one. A plain `CorrelationId` index is added to `SagaInstances`
to serve the new resolve-by-correlation-id lookup, which the composite key can't answer (its leading
column is `SagaType`).

**Still open, for the same reason as before:** no choreographed saga is wired into the `OrderProcessing`
sample yet. The keyspace no longer blocks it, so what remains is genuine sample design — deciding what
an independent choreographed process over these messages should actually be — rather than a constraint
of the engine. The engine-level capability is covered by `SagaIdentityScopingTests`, which runs an
orchestrated and a choreographed saga in one engine under a single shared correlation id.

> **Done in the next pass** — see [`choreography-in-orderprocessing-sample.md`](choreography-in-orderprocessing-sample.md) below.
