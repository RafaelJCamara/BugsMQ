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
    /// This exists for the case NotifyParentAsync structurally cannot cover — the child's own timeout,
    /// which never calls it (see InvoiceArchivalSaga's doc comment) — so this is the only rescue then.
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
                .Finalize(SagaStatus.Completed);

        WithTimeout(AwaitingArchival, ArchivalWaitTimeout,
            t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}
