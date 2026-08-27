using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;
using VSaga.Samples.OrderProcessing.Contracts;

namespace VSaga.Samples.OrderProcessing;

public sealed class InvoiceArchivalState : SagaState
{
    public string? OrderId { get; set; }
}

/// <summary>
/// A second, independent sub-saga off <see cref="InvoiceIssued"/> — filing a copy of the invoice for
/// accounting, a different concern from <see cref="InvoiceDeliverySaga"/> getting it into the
/// customer's hands. <see cref="InvoiceFollowUpSaga"/> starts one per invoice via
/// <c>ctx.StartChildAsync</c> and, unlike <c>PostShipmentChoreography</c>, actually waits for the
/// result — see that saga's doc comment for why this is a different child rather than a second
/// consumer of <see cref="InvoiceDeliverySaga"/> (reusing it would file two customer emails for one
/// invoice, not two archive copies).
///
/// <para>
/// <b>Reports back via <c>ctx.NotifyParentAsync</c></b> on both terminal endings that reach a real
/// step — <see cref="StoreInvoiceCopy"/> succeeding or failing. Deliberately not on timeout: a child
/// that never hears back from the archive store never reaches any step to call NotifyParentAsync from,
/// which is exactly the structural gap docs/sub-saga-composition.md §3.4 attributes to option (a). As of
/// Slice 2b, that gap has an engine-level answer instead of an author-level one: this saga's own
/// <c>SagaOrchestrator</c> publishes <c>ChildSagaFinished</c> to <see cref="InvoiceFollowUpSaga"/> when
/// this timeout fires, without this saga's own code doing anything — see that class's doc comment.
/// </para>
/// </summary>
public sealed class InvoiceArchivalSaga : OrchestratedSagaDefinition<InvoiceArchivalState>
{
    public State<InvoiceArchivalState> Requested { get; }
    public State<InvoiceArchivalState> AwaitingStorage { get; }
    public State<InvoiceArchivalState> Archived { get; }
    public State<InvoiceArchivalState> Failed { get; }

    /// <summary>
    /// Shorter than InvoiceFollowUpSaga's own 30s wait timeout, deliberately: the common case should be
    /// this child noticing a stalled archive store and reporting Failed before the parent's own timeout
    /// ever has to fire. A silently dropped StoreInvoiceCopy still leaves this child's own timeout as
    /// the only thing that fires, and — per the doc comment above — it does not notify the parent, so
    /// that case demonstrates the parent's timeout doing real work, not this one's.
    /// </summary>
    private static readonly TimeSpan StorageTimeout = TimeSpan.FromSeconds(15);

    public InvoiceArchivalSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingStorage = State(nameof(AwaitingStorage));
        Archived = State(nameof(Archived));
        Failed = State(nameof(Failed));

        During(Requested)
            .When<ArchiveInvoice>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.Saga.OrderId = m.OrderId)
                .Publish((ctx, m) => new StoreInvoiceCopy(ctx.CorrelationId, m.OrderId, m.InvoiceNumber))
                .TransitionTo(AwaitingStorage);

        During(AwaitingStorage)
            .When<InvoiceCopyStored>()
                .Then((ctx, _) => ctx.NotifyParentAsync(new InvoiceArchivalFinished(ctx.Saga.OrderId!, Archived: true), ctx.CancellationToken))
                .TransitionTo(Archived)
                .Finalize(SagaStatus.Completed)
            .When<InvoiceCopyStorageFailed>()
                .Then((ctx, _) => ctx.NotifyParentAsync(new InvoiceArchivalFinished(ctx.Saga.OrderId!, Archived: false), ctx.CancellationToken))
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        WithTimeout(AwaitingStorage, StorageTimeout, t => t.TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
    }
}
