# Design: sub-saga composition

**Status: Slice 1 is built and shipped** — see the README's "Sub-saga composition: parent linkage"
section, which is the authoritative description of what exists. Slices 2 and 3 remain proposed, and
this file is now only about those, plus what building Slice 1 taught about them.

**One claim in the original sketch was wrong, and it bears directly on the §3.4 decision.** §3.4(a)
said a child publishing its own domain message "works today with no engine change, once the child knows
its parent id." It does not. `SagaContext.PublishAsync` always stamps the *publishing* saga's
correlation id, and `SagaOrchestrator.HandleCoreAsync` correlates strictly on the inbound correlation
id — so a child publishing "I'm done" sends it under the child's own id, where the parent never sees
it. `CorrelateBy` is not a fallback: it is documented as a business key for dashboard search,
explicitly not used for routing.

Following that through changed the shape of the §3.4 decision twice over: **(b), not (a), is the option
needing no new public API** (the orchestrator already holds the transport and can stamp any envelope),
and the two options turn out to be **complementary rather than alternatives** — only (a) can carry the
child's domain result, and only (b) can fire when a child fails, because a failed child never reaches
its own publish step. The original recommendation of "(a) first, (b) later" survives, on entirely
different reasoning. Worked through in §3.4; summarised in §7.1.

Written to be picked up cold in a later session: every claim about the current codebase carries a
`file:line` so it can be re-checked rather than trusted. Line numbers in §2 were accurate at commit
`f00dee3` and have since drifted — re-grep rather than trusting them.

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
does not silently extend it (a self-transition neither cancels nor reschedules).

**Still true, but only for the parent's half.** The parent can park and be released by a message. What
has no machinery is *the child getting a message to the parent in the first place* — see the
correction in §3.4. So "no new engine work" describes the waiting, not the notifying, and the section
title oversold it.

### 3.4 Notifying the parent — **OPEN DECISION**

> **Corrected after building Slice 1.** Option (a) does *not* work with no engine change — see the
> status note at the top of this file. A child cannot address its parent at all today, so (a) needs a
> way to publish under the parent's correlation id before it is even expressible.

**(a) Child publishes its own domain message.** ~~Works today with no engine change~~ — needs a new
method on `ISagaContext` that publishes under `Saga.ParentCorrelationId` (that field does now exist,
and is populated). Typed; the parent matches `When<PaymentSettled>()`. Every child must remember to do
it, and a child that forgets leaves the parent hanging until its own timeout.

**(b) Engine auto-publishes `ChildSagaFinished(childCorrelationId, childSagaType, status)`** to the
parent when a child with a parent reaches a terminal status. Uniform; works for children unaware they
are children. But it is *one CLR type*, so a parent awaiting two different child types must branch on a
string field inside `.Then(...)` rather than on message type — against the grain of this DSL.

#### The cost comparison runs the opposite way to the original framing

**(b) needs no new public API at all.** `SagaOrchestrator` already holds the transport and can stamp
any envelope it likes, and it already has the child's `ParentCorrelationId` on the state — so (b) is a
terminal-status hook, a contract type, and an append-only `SagaEntryType`. It is **(a)** that adds
public surface to `ISagaContext`. The original sketch had this backwards, and so did the first attempt
at correcting it.

#### …but the decision does not turn on cost, it turns on payload

**`ChildSagaFinished` carries no domain data.** A parent that started a `PaymentSaga` wants to know
*what was charged*, not merely that it finished. Under (b) alone its only recourse is reading the
child's state, which it cannot do generically: `ISagaSummaryReader` is saga-type-agnostic and hands
back untyped `DataJson`. So (b) alone is insufficient for most real waits however cheap it is.

**And a failed child never reaches its "publish my result" step**, by construction — so (a) cannot
cover failure or timeout, however typed it is.

**They are complementary, not alternatives**, which is what the original either/or framing missed:

| | carries the domain result | fires when the child fails or times out |
|---|---|---|
| (a) child publishes | yes | no — the child never gets there |
| (b) engine publishes | no | yes |

**Recommendation: (a) as the primary path, (b) afterwards as the failure net.** Same conclusion the
original sketch reached, reached for a sound reason this time — not "(a) is free" (it is not), but
"only (a) can carry the answer back."

Two things to settle before building (a):

1. **Narrow the API.** Not `PublishAsync(message, correlationId)` — that lets any saga publish under
   any id and mint orphan instances, dissolving the invariant that a saga's outbound messages carry its
   own correlation id. Prefer `ctx.NotifyParentAsync(message)`, which reads `ParentCorrelationId`
   itself and fails loudly on a root saga. Identical engine work; the only foreign id a saga can
   publish under is one the engine already put on its state.
2. **The notification fans out.** Published under the parent's correlation id, it reaches *every* saga
   type tracking that id — in the sample, `OrderSaga` as well as `PostShipmentChoreography`. That is
   the same fan-out any published message has, but it needs documenting rather than discovering.

**A hazard specific to (b):** `UnhandledEventPolicy.Throw` makes an unhandled message nack and
redeliver (`src/BugsMQ.Core/Dsl/UnhandledEventPolicy.cs`). A parent that opted into `Throw` and does
not handle `ChildSagaFinished` at its current state would spin on a message its author never asked
for. So (b) has to be opt-in per parent, not a global engine behaviour.

**Status: recommended, not decided.** Nobody has signed off on the above.

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

#### Three further arguments, from having built Slice 1

1. **The parent genuinely does not know its children.** `StartChildAsync` returns `Task`, not the
   child's id, and there is no compile-time link. A cascade would therefore mean the engine issuing a
   *database query* mid-compensation to discover which side effects to send — and that query returns
   children still `Running`, children that already self-compensated on their own failure, and (where
   two saga types initiate on one child message) children the parent never intended to start.
2. **Unbounded depth, no cycle detection.** Slice 1 deliberately gives each child a fresh correlation
   id precisely so a saga can start its own type; a cascade walks that tree with nothing bounding it.
3. **Slice 1 shipped with child failure not touching the parent** — verified live across all 43
   parent/child pairs, every parent `Completed` regardless of whether its child completed, bounced, or
   timed out. A cascade adds implicit coupling in the reverse direction, and with both directions
   implicit nobody can predict what a single `.Compensate()` sends.

**Status: still recommended, still not decided.**

---

## 4. Slices

### Slice 1 — parent linkage — **DONE**

- [x] `SagaState`: add `ParentSagaType` / `ParentCorrelationId`
- [x] `MessageEnvelope`: the two header name constants
- [x] `ISagaContext.StartChildAsync` + implementation in `SagaContext`
- [x] `SagaOrchestrator`: read the headers and stamp the new instance (in `NewInstance`, extracted from
      `HandleCoreAsync` to stay under the 60-line analyzer cap)
- [x] `SagaInstanceEntity` + `BugsMqDbContext`: nullable columns, index on the pair
- [x] Migration `20260825121440_AddSagaParentLinkage` — purely additive, upgrades an existing volume in place
- [x] `ISagaSummaryReader.FindChildrenAsync` + both providers
- [x] `GET /api/sagas/{sagaType}/{correlationId}/children`
- [x] Dashboard: "started by" / "started" strips alongside the existing correlation-id one
- [x] Tests: `SubSagaCompositionTests` (real publish/receive path), children query in both providers,
      endpoint tests, Angular tests. Mutation-verified from both ends of the wire.
- [x] Sample: `PostShipmentChoreography` starts an `InvoiceDeliverySaga`. Answers §7.3/§7.4 narrowly —
      a demonstration was needed for the §6 live verification to be possible at all, and it was added
      without touching `OrderSaga`, leaving the "should `OrderSaga` itself be restructured" question open.

**Columns, not just `DataJson`:** `ISagaSummaryReader` is saga-type-agnostic and queries columns, so a
tree view needs real columns. The `DataJson` blob picks the new fields up for free (additive), but that
alone is not queryable.

**One thing worth knowing before Slice 2:** `SagaSummary` gained the two fields as *required* positional
parameters rather than defaulted ones, deliberately, so every projection site had to decide what to put
there instead of silently defaulting to null. That is a source-breaking change to a public record.

### Slice 2 — completion notification

Split in two after the §3.4 analysis, because the two halves cover different cases and 2a is the one
that makes a wait usable at all. Build 2a first, so the sample demonstrates a real wait before the
engine starts injecting messages on anyone's behalf.

**Slice 2a — the child reports its own result (the primary path)**

- [ ] `ISagaContext.NotifyParentAsync(message)` — publishes under `Saga.ParentCorrelationId`, fails
      loudly on a root saga. Deliberately *not* a general publish-under-any-id overload; see §3.4
- [ ] A parent in the sample that actually waits, via the existing join
      (`.TransitionTo(s => s.ChildDone ? Ready : AwaitingChild)`) plus a timeout on the waiting state
- [ ] Tests through the real publish/receive path, and the fan-out note from §3.4 documented

**Slice 2b — the engine reports failures the child could not (the safety net)**

- [ ] `ChildSagaFinished` contract
- [ ] Orchestrator publishes it when a child with a parent goes terminal — **opt-in per parent**, since
      a parent using `UnhandledEventPolicy.Throw` would otherwise spin on an engine-injected message
      its author never asked for
- [ ] `SagaEntryType`: `ChildSagaStarted`, `ChildSagaFinished` — **APPEND ONLY.** The enum persists as
      plain integers; inserting a member silently reinterprets every existing row
- [ ] Dashboard timeline + saga map rendering for the child hop. Note Slice 1 shipped without this:
      a started child currently logs as an ordinary `MessagePublished`

### Slice 3 — compensation cascade (push back before starting)

- [ ] `.CompensateChildren()` on the parent's failure step
- [ ] Child-side hook to receive a compensation request
- [ ] Ordering and double-compensation semantics decided and documented **first**

---

## 5. Failure modes — document, don't discover

- **Child never starts** (nothing's `CanInitiate` matched). Parent hangs until its timeout.
  Unpreventable at publish time; must be documented. **Now pinned by a test**
  (`AChildMessageNobodyInitiatesOn_StartsNothingAndTellsNobody`) and described in the README, so it is
  documented behaviour rather than a surprise.
- **Two saga types initiate on the child message** → two children, parent counts one. Wants a guard or
  a loud note. Compare the `AddSaga` duplicate-`TState` guard added in `f00dee3`
  (`src/BugsMQ.Core/ServiceCollectionExtensions.cs`), which turned a similar silent misbehaviour into a
  startup error. **Still unguarded** after Slice 1 — noted in the README, not solved.
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

Required, and **all three were done for Slice 1** — see the README section for the results:

1. Tests that drive the **real** publish → receive → create-instance path and assert
   `ParentSagaType`/`ParentCorrelationId` on the persisted snapshot.
2. Live `docker compose up` run with a real parent/child pair, then
   `curl .../children` and inspect the actual rows.
3. Mutation check, per this repo's habit: make the orchestrator ignore the parent headers and confirm
   the linkage tests fail — and *only* those.

**Worth keeping for Slice 2, because the mutation run confirmed the scar is still live.** Under both
mutations the EF Core provider tests and the dashboard endpoint tests stayed green, because they build
the parent link by hand. That is correct for their subject — the store and the route — but it means
they cannot catch a linkage that is never set, which is exactly how the `CausationId` tests passed
against a header nobody read. Any Slice 2 test for `NotifyParentAsync` has the same trap available to
it: a test that hand-publishes under the parent's correlation id proves nothing about whether
`NotifyParentAsync` reads `ParentCorrelationId`.

---

## 7. Open questions

1. **§3.4 — analysed, awaiting sign-off.** The two options turned out to be complementary rather than
   alternatives: only (a) can carry the child's domain result, and only (b) can fire when a child fails
   or times out (since a failed child never reaches its own publish step). Recommendation is therefore
   **(a) as the primary path, (b) afterwards as the failure net** — the same conclusion the original
   sketch reached, but for a sound reason rather than for "(a) is free", which it is not. Note the cost
   comparison also runs the opposite way to the original framing: (b) needs no new public API, (a)
   does. See §3.4 in full, including the narrowed `NotifyParentAsync` shape and the
   `UnhandledEventPolicy.Throw` hazard that makes (b) opt-in per parent.
2. **§3.5 — analysed, awaiting sign-off.** Recommendation unchanged: no automatic cascade. Building
   Slice 1 added three arguments for it rather than against — the parent does not know its children
   without a mid-compensation database query, the tree is unbounded in depth with self-recursion
   deliberately enabled, and Slice 1 shipped with child failure not touching the parent, verified live.
   See §3.5.
3. ~~Is Slice 1 alone worth shipping without a sample demonstrating it?~~ Settled by necessity: §6's
   live verification requires a real parent/child pair in the running stack, so the sample wiring could
   not be split off the way the choreographed DSL's was.
4. **Which sample?** Partly answered. `InvoiceDeliverySaga` demonstrates it additively, off
   `PostShipmentChoreography`. The original question — whether `OrderSaga` itself should be
   restructured around sub-sagas — is still open and still a product decision about what the sample is
   *for*, the same one the parallel fan-out work left open.
5. **New:** should the parent be able to learn its child's correlation id? `StartChildAsync` returns
   `Task`, not `Task<Guid>`, matching this design. Nothing needs the id yet — the relation is queried
   parent-to-child via `FindChildrenAsync`, and option §3.4(a) has the child address the parent rather
   than the reverse. Slice 2 may change that, and it is a one-line signature change if so.
