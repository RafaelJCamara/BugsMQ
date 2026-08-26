using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

public sealed record ChoreoInventoryOutOfStock(string OrderId);
public sealed record ChoreoPaymentDeclined(string OrderId);
public sealed record ChoreoFlakyEvent(string OrderId);
public sealed record ChoreoOrderPlaced(string OrderId);
public sealed record ChoreoInventoryReserved(string OrderId);
public sealed record ChoreoPaymentCharged(string OrderId);
public sealed record ChoreoReleaseInventory(string OrderId);

public sealed class ChoreoShippingState : SagaState
{
    public string? OrderId { get; set; }

    public bool InventoryReady { get; set; }

    public bool PaymentReady { get; set; }
}

/// <summary>
/// A choreography with no central conductor: OrderService, InventoryService, and PaymentService each
/// publish their own domain events independently (no one commands "ReserveInventory"/"ChargePayment")
/// — this definition just tracks the resulting flow for the dashboard/timeline. Reactions are
/// registered per event type only via <c>On&lt;T&gt;()</c>, not gated to a specific state the way
/// <c>OrchestratedSagaDefinition.During(state).When&lt;T&gt;()</c> gates its steps — so, unlike an
/// orchestrated saga, either InventoryReserved or PaymentCharged can be observed first (three
/// independent services racing over a real broker have no reason to agree on an order), and each is
/// still handled correctly regardless of which one the tracker sees first.
/// </summary>
public sealed class TestShippingChoreography : ChoreographedSagaDefinition<ChoreoShippingState>
{
    public State<ChoreoShippingState> Tracking { get; }
    public State<ChoreoShippingState> Reserved { get; }
    public State<ChoreoShippingState> Charged { get; }
    public State<ChoreoShippingState> Completed { get; }
    public State<ChoreoShippingState> Failed { get; }

    /// <summary>Attempt counter for the in-process, step-level RetryPolicy test.</summary>
    public int FlakyAttempts { get; set; }

    public TestShippingChoreography()
    {
        Tracking = InitialState(nameof(Tracking));
        Reserved = State(nameof(Reserved));
        Charged = State(nameof(Charged));
        Completed = State(nameof(Completed));
        Failed = State(nameof(Failed));

        On<ChoreoOrderPlaced>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId);

        On<ChoreoInventoryReserved>()
            .StartsNewInstance() // InventoryService may be observed before OrderPlaced ever reaches this tracker
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.InventoryReady = true)
            .RecordState(Reserved);

        On<ChoreoPaymentCharged>()
            .StartsNewInstance() // PaymentService is equally independent — either can be first
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.PaymentReady = true)
            .RecordState(Charged)
            .Finalize(SagaStatus.Completed);

        On<ChoreoInventoryOutOfStock>()
            .Compensate()
            .RecordState(Failed)
            .Finalize(SagaStatus.Failed);

        On<ChoreoPaymentDeclined>()
            .Compensate()
            .RecordState(Failed)
            .Finalize(SagaStatus.Failed);

        On<ChoreoFlakyEvent>()
            .Then((_, _) =>
            {
                FlakyAttempts++;
                if (FlakyAttempts < 2)
                    throw new InvalidOperationException("simulated transient failure");
            })
            .Retry(RetryPolicy.Exponential(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1)))
            .RecordState(Reserved);

        Compensate(Reserved, (ctx, ct) => ctx.PublishAsync(new ChoreoReleaseInventory(ctx.Saga.OrderId!), ct));

        WithTimeout(Reserved, TimeSpan.FromMinutes(5), t => t.Compensate().TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
    }
}
