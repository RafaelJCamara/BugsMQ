using BugsMQ.Abstractions.Sagas;
using BugsMQ.Core.Dsl;
using BugsMQ.Samples.OrderProcessing.Contracts;

namespace BugsMQ.Samples.OrderProcessing;

public sealed class InvoiceFollowUpState : SagaState
{
    public string? OrderId { get; set; }

    /// <summary>Null while waiting; set from the child's actual result once InvoiceArchivalFinished arrives.</summary>
    public bool? InvoiceArchived { get; set; }
}

/// <summary>
/// Slice 2a's live demonstration of a parent that actually waits (docs/sub-saga-composition.md §4,
/// Slice 2a), alongside <see cref="PostShipmentChoreography"/>'s Slice 1 demonstration of one that
/// deliberately does not. Both react to <see cref="InvoiceIssued"/>, and both therefore run under the
/// same shared order correlation id — the ordinary consequence of that id being shared by every saga
/// tracking one order, not something wired here.
///
/// <para>
/// <b>Why a new saga rather than teaching <see cref="PostShipmentChoreography"/> to wait.</b> That
/// saga's own doc comment is explicit that its leg must complete once all three post-shipment services
/// have reported, and an undeliverable invoice must not hold it open — correct, and not something this
/// pass changes. Waiting needs a state that parks, which is the orchestrated DSL's
/// <c>During(state).When&lt;T&gt;().TransitionTo(...)</c> shape (docs/sub-saga-composition.md §3.3), not
/// the choreography's <c>On&lt;T&gt;().Finalize(Func)</c> join — retrofitting a park onto the
/// choreography would mean either blocking its completion on archival (contradicting its own
/// documented invariant) or reacting to a message after the instance is already terminal, which is not
/// the parking-and-release pattern Slice 2a is meant to demonstrate.
/// </para>
///
/// <para>
/// <b>Why archival, not another delivery.</b> Reusing <see cref="InvoiceDeliverySaga"/> as this saga's
/// child too would start a second <see cref="DeliverInvoice"/>-shaped flow per invoice — two customer
/// emails for one invoice, not a second observer of the first. Filing a copy for accounting is a
/// distinct, real concern with its own real failure mode (the archive store being unavailable), so it
/// gets its own child, <see cref="InvoiceArchivalSaga"/>.
/// </para>
///
/// <para>
/// <b>Two states, not one, for the state that starts the child.</b> Same reason
/// <c>PostShipmentChoreography</c>'s own doc comment gives: the orchestrator only schedules a timeout on
/// a real transition, and starting the child from a self-transitioning initial state would schedule
/// none.
/// </para>
///
/// <para>
/// <b>Slice 2b: also opts into the engine's ChildSagaFinished safety net.</b>
/// <see cref="InvoiceArchivalSaga"/>'s own <c>WithTimeout</c> deliberately never calls
/// <c>NotifyParentAsync</c> (see its doc comment) — declaring a <c>.When&lt;ChildSagaFinished&gt;()</c>
/// handler here is what makes that engine-published event reach this saga at all (a parent that never
/// declares a handler for it is never even subscribed), and it rescues this wait in ~15s instead of the
/// full 30s <see cref="ArchivalWaitTimeout"/> would otherwise take.
/// </para>
/// </summary>
public sealed class InvoiceFollowUpSaga : OrchestratedSagaDefinition<InvoiceFollowUpState>
{
    public State<InvoiceFollowUpState> Requested { get; }
    public State<InvoiceFollowUpState> AwaitingArchival { get; }
    public State<InvoiceFollowUpState> Archived { get; }
    public State<InvoiceFollowUpState> Abandoned { get; }

    /// <summary>
    /// Longer than InvoiceArchivalSaga's own 15s storage timeout, deliberately: the common case is the
    /// child noticing a stalled archive store and reporting Failed well before this ever has to fire.
    /// As of Slice 2b, this is no longer the only rescue for the child's own timeout — the engine's
    /// ChildSagaFinished safety net (see the class doc comment) reaches this saga in ~15s instead. This
    /// timeout remains as the backstop for whatever ChildSagaFinished itself cannot cover (the child
    /// process crashing outright, or this parent's own message never arriving at all).
    /// </summary>
    private static readonly TimeSpan ArchivalWaitTimeout = TimeSpan.FromSeconds(30);

    public InvoiceFollowUpSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingArchival = State(nameof(AwaitingArchival));
        Archived = State(nameof(Archived));
        Abandoned = State(nameof(Abandoned));

        During(Requested)
            .When<InvoiceIssued>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.Saga.OrderId = m.OrderId)
                .Then((ctx, m) => ctx.StartChildAsync(new ArchiveInvoice(m.OrderId, m.InvoiceNumber), ctx.CancellationToken))
                .TransitionTo(AwaitingArchival);

        During(AwaitingArchival)
            .When<InvoiceArchivalFinished>()
                .Then((ctx, m) => ctx.Saga.InvoiceArchived = m.Archived)
                .TransitionTo(Archived)
                .Finalize(SagaStatus.Completed)
            // The Slice 2b safety net: InvoiceArchivalSaga's own WithTimeout deliberately never calls
            // NotifyParentAsync (see its doc comment), so this engine-published ChildSagaFinished is the
            // only thing that can rescue this wait before ArchivalWaitTimeout — 15s into this state
            // instead of 30. Declaring this handler is what opts InvoiceFollowUpSaga in at all: the
            // engine only ever delivers ChildSagaFinished to a parent that asked for it somewhere in its
            // own DSL, per docs/sub-saga-composition.md's Slice 2b design.
            .When<ChildSagaFinished>()
                .Then((ctx, _) => ctx.Saga.InvoiceArchived = false)
                .TransitionTo(Abandoned)
                .Finalize(SagaStatus.TimedOut);

        WithTimeout(AwaitingArchival, ArchivalWaitTimeout,
            t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}
