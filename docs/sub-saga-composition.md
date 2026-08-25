# Design: sub-saga composition

**Status:** proposed, nothing built. **Author's recommendation:** build Slice 1, then re-evaluate.

This is a design sketch, not a record of shipped work — which is why it lives here rather than in
`README.md`, where every section describes something that exists. If this gets built, the outcome
belongs in the README in that same voice and this file should be deleted or reduced to a pointer.

Written to be picked up cold in a later session: every claim about the current codebase carries a
`file:line` so it can be re-checked rather than trusted. Line numbers were accurate at commit
`f00dee3`; re-grep if they have drifted.

---

## 1. What it is

One saga starts another as a step, waits for it to finish, and resumes with its result — the parent
treats a whole child saga as a single logical step.

Concretely here: `OrderSaga` reaching `AwaitingPayment` could start a separate `PaymentSaga` with its
own retries, gateway-fallback states, and compensation, then wait for it to report back.

The motivations are reuse (one `RefundSaga` invoked by several parents), decomposition (keeping a
20-state flow readable), and independent lifecycles (a child with its own timeout/retry policy that
doesn't pollute the parent's state machine).

Its only trace in the repo today is the roadmap line in `README.md` — "additional transport adapters
(MassTransit/Wolverine) and sub-saga composition" — inherited from the original v1 commit's note.
There is no design or code behind it.

---

## 2. What already exists that this builds on

Three recent passes built most of the machinery without aiming at this. Read these before designing
anything new — the intent is to reuse patterns, not invent parallel ones.

| Capability | Where | Why it matters here |
|---|---|---|
| Composite `(SagaType, CorrelationId)` identity | `src/BugsMQ.Persistence.EFCore/BugsMqDbContext.cs:28` | Two sagas can already coexist over one business transaction |
| Join / "wait for all branches" | `src/BugsMQ.Core/Dsl/StepDefinition.cs:31` (`ResolveTargetState`), `EventBuilder.TransitionTo(Func<...>)` | **The parent's wait needs no new engine work** |
| Header threading onto new instances | `src/BugsMQ.Core/Runtime/SagaOrchestrator.cs:127,135,309,336` | Exact pattern the parent link should copy |
| Outbound envelope stamping | `src/BugsMQ.Core/Runtime/SagaContext.cs:53`, `MessageEnvelope.From` | Where `StartChildAsync` hangs off |
| Cross-instance lookup for the dashboard | `ISagaSummaryReader.FindByCorrelationIdAsync` (`ISagaSummaryReader.cs:27`) | Precedent for a `FindChildrenAsync` |
| Related-saga UI strip | `dashboard-web/src/app/pages/saga-detail/saga-detail.ts:104` | Becomes two relations instead of one |
| Migration precedent | `src/BugsMQ.Persistence.EFCore.Postgres/Migrations/20260825045219_*` | Adding columns + index to `SagaInstances` |

---

## 3. Design

### 3.1 Identity

A child is a **separate instance with its own correlation id**, plus a stored pointer to its parent:

```csharp
// SagaState — src/BugsMQ.Abstractions/Sagas/SagaState.cs
public string? ParentSagaType { get; set; }
public Guid? ParentCorrelationId { get; set; }   // both null => root saga
```

**Rejected: child shares the parent's correlation id.** The primary key is
`(SagaType, CorrelationId)`, so sharing caps a parent at one child *per saga type*, and a
self-recursive saga (a `RefundSaga` starting a `RefundSaga`) collides outright.

### 3.2 Starting a child

The parent must not need the child's `TState` or definition type. It publishes the child's initiating
message with linkage on the envelope:

```csharp
// new on ISagaContext<TState>
Task StartChildAsync<TMessage>(TMessage message, CancellationToken ct = default) where TMessage : notnull;
```

Implementation: publish with a **fresh** correlation id and two new headers on `MessageEnvelope`:

```
x-bugsmq-parent-saga-type
x-bugsmq-parent-correlation-id
```

`SagaOrchestrator.HandleCoreAsync` reads them when creating an instance and stamps them onto the new
state — the same shape as `GetSourceService`/`GetCausationId` today.

Whichever saga's `CanInitiate` matches the message becomes the child; no compile-time link exists. This
is deliberate and matches how the dashboard's retry redrive already works (publish, let subscribers
pick it up).

### 3.3 Waiting — no new engine work

This is the join primitive from `f00dee3`:

```csharp
During(AwaitingChildren)
    .When<PaymentSettled>()
        .Then((ctx, _) => ctx.Saga.PaymentDone = true)
        .TransitionTo(s => s.AllChildrenDone ? Ready : AwaitingChildren);
```

Self-transition semantics are already right: one timeout covers the whole wait, and an arriving child
does not silently extend it (`SagaOrchestrator.cs:400` — a self-transition neither cancels nor
reschedules).

### 3.4 Notifying the parent — **OPEN DECISION**

**(a) Child publishes its own domain message.** Works today with no engine change, once the child knows
its parent id. Typed; the parent matches `When<PaymentSettled>()`. Every child must remember to do it.

**(b) Engine auto-publishes `ChildSagaFinished(childCorrelationId, childSagaType, status)`** to the
parent when a child with a parent reaches a terminal status. Uniform; works for children unaware they
are children. But it is *one CLR type*, so a parent awaiting two different child types must branch on a
string field inside `.Then(...)` rather than on message type — against the grain of this DSL.

**Recommendation:** ship (a) first; add (b) later as a safety net, not the primary path.

### 3.5 Compensation cascade — **OPEN DECISION, the hard one**

Does a parent's `.Compensate()` cascade into completed children?

**Recommendation: no, not automatically.** An engine that publishes compensating side effects on your
behalf, for children it merely *believes* completed, is the same failure class as commit `c24928d`
("Fix saga timeout/message race that could ship and refund the same order"). Ordering across a tree is
ambiguous (depth-first? completion order?), and a child may already have self-compensated on its own
failure — so automatic cascade is a double-refund generator. The parent explicitly publishing its own
compensating command is boring and correct.

If it is ever wanted, it should be explicit opt-in (`.CompensateChildren()`) plus a child-side hook to
receive the request — that is Slice 3, and it is the one I would push back on.

---

## 4. Slices

### Slice 1 — parent linkage (recommended; low risk, useful alone)

- [ ] `SagaState`: add `ParentSagaType` / `ParentCorrelationId` (`src/BugsMQ.Abstractions/Sagas/SagaState.cs`)
- [ ] `MessageEnvelope`: add the two header name constants (`src/BugsMQ.Abstractions/Transport/MessageEnvelope.cs`)
- [ ] `ISagaContext.StartChildAsync` + implementation in `SagaContext` (`src/BugsMQ.Core/Runtime/SagaContext.cs`)
- [ ] `SagaOrchestrator`: read the headers and stamp the new instance, mirroring `GetSourceService`/`GetCausationId`
- [ ] `SagaInstanceEntity` + `BugsMqDbContext`: nullable columns, index on `(ParentSagaType, ParentCorrelationId)`
- [ ] `dotnet ef migrations add AddSagaParentLinkage --project src/BugsMQ.Persistence.EFCore.Postgres --startup-project src/BugsMQ.Dashboard.Api`
- [ ] `ISagaSummaryReader.FindChildrenAsync(parentSagaType, parentCorrelationId)` + both providers
- [ ] `GET /api/sagas/{sagaType}/{correlationId}/children`
- [ ] Dashboard: extend the related-sagas strip into "started by" / "started"
- [ ] Tests: linkage stamped through the **real** publish/receive path; children query; DSL surface

**Columns, not just `DataJson`:** `ISagaSummaryReader` is saga-type-agnostic and queries columns, so a
tree view needs real columns. The `DataJson` blob picks the new fields up for free (additive), but that
alone is not queryable.

### Slice 2 — automatic completion notification

- [ ] `ChildSagaFinished` contract
- [ ] Orchestrator publishes it when a child with a parent goes terminal
- [ ] `SagaEntryType`: `ChildSagaStarted`, `ChildSagaFinished` — **APPEND ONLY.** The enum persists as
      plain integers; inserting a member silently reinterprets every existing row
      (`src/BugsMQ.Abstractions/Persistence/SagaEntryType.cs:25`)
- [ ] Dashboard timeline + saga map rendering for the child hop

### Slice 3 — compensation cascade (push back before starting)

- [ ] `.CompensateChildren()` on the parent's failure step
- [ ] Child-side hook to receive a compensation request
- [ ] Ordering and double-compensation semantics decided and documented **first**

---

## 5. Failure modes — document, don't discover

- **Child never starts** (nothing's `CanInitiate` matched). Parent hangs until its timeout.
  Unpreventable at publish time; must be documented.
- **Two saga types initiate on the child message** → two children, parent counts one. Wants a guard or
  a loud note. Compare the `AddSaga` duplicate-`TState` guard added in `f00dee3`
  (`src/BugsMQ.Core/ServiceCollectionExtensions.cs`), which turned a similar silent misbehaviour into a
  startup error.
- **Parent times out while the child still runs** → orphaned child, still holding whatever it reserved.
- **Parent retried from the dashboard** → children are *not* re-run; the reset replays the parent's
  own message only.
- **Adding a timeout to an existing state does not rescue in-flight instances** — timeouts are scheduled
  on entry to a state. Same trap as the 60 stranded sagas in the "Timeout coverage for every awaiting
  state" README section.

---

## 6. Verification plan

**Unit/integration tests are not sufficient for the linkage.** This repo has a specific scar here: a
previous pass threaded `SourceService`/`CausationId` onto envelope headers, and tests that hand-built
`SagaLogEntry` objects with the field already populated passed while the orchestrator never actually
read the header. Any test that constructs the parent link by hand proves nothing.

Required:

1. Tests that drive the **real** publish → receive → create-instance path and assert
   `ParentSagaType`/`ParentCorrelationId` on the persisted snapshot.
2. Live `docker compose up` run with a real parent/child pair, then
   `curl .../children` and inspect the actual rows.
3. Mutation check, per this repo's habit: make the orchestrator ignore the parent headers and confirm
   the linkage tests fail — and *only* those.

---

## 7. Open questions for the next session

1. **§3.4** — auto `ChildSagaFinished`, or child publishes its own domain message? (recommendation: the
   latter first)
2. **§3.5** — compensation cascade: confirm "no automatic cascade" is acceptable.
3. Is Slice 1 alone worth shipping without a sample demonstrating it? The choreographed DSL shipped one
   pass ahead of its sample wiring (`0c5fe38` → `a87f40e`) and that split worked well.
4. Which sample would demonstrate this? `OrderSaga` is the reference for the *linear* shape and several
   README sections describe its exact compensation ordering — restructuring it is a product decision
   about what the sample is for, the same question left open for the parallel fan-out work.
