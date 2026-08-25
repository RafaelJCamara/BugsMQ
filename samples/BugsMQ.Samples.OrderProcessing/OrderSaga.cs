using BugsMQ.Abstractions.Sagas;
using BugsMQ.Core.Dsl;
using BugsMQ.Samples.OrderProcessing.Contracts;

namespace BugsMQ.Samples.OrderProcessing;

public sealed class OrderSagaState : SagaState
{
    public string? OrderId { get; set; }

    public string? CustomerId { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>
/// Order -> reserve inventory -> charge payment -> ship -> done, with compensation on any downstream
/// failure and a timeout on AwaitingPayment (the payment participant occasionally simulates a hung
/// gateway to exercise it). This is the reference orchestrated saga the whole v1 slice is validated against.
/// </summary>
public sealed class OrderSaga : OrchestratedSagaDefinition<OrderSagaState>
{
    public State<OrderSagaState> Submitted { get; }
    public State<OrderSagaState> AwaitingInventory { get; }
    public State<OrderSagaState> AwaitingPayment { get; }
    public State<OrderSagaState> AwaitingShipment { get; }
    public State<OrderSagaState> Completed { get; }
    public State<OrderSagaState> Failed { get; }

    /// <summary>
    /// How long any one participant gets to reply before its state is treated as stalled. Generous
    /// against the participants' 150–500ms simulated work and chaos mode's 4s inbound delay ceiling, so
    /// it fires on genuinely lost messages rather than on ordinary slowness.
    /// </summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    public OrderSaga()
    {
        Submitted = InitialState(nameof(Submitted));
        AwaitingInventory = State(nameof(AwaitingInventory));
        AwaitingPayment = State(nameof(AwaitingPayment));
        AwaitingShipment = State(nameof(AwaitingShipment));
        Completed = State(nameof(Completed));
        Failed = State(nameof(Failed));

        During(Submitted)
            .When<OrderSubmitted>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) =>
                {
                    ctx.Saga.CustomerId = m.CustomerId;
                    ctx.Saga.Amount = m.Amount;
                })
                .Publish((ctx, m) => new ReserveInventory(ctx.CorrelationId, m.OrderId))
                .TransitionTo(AwaitingInventory);

        During(AwaitingInventory)
            .When<InventoryReserved>()
                .Publish((ctx, _) => new ChargePayment(ctx.CorrelationId, ctx.Saga.OrderId!, ctx.Saga.Amount))
                .TransitionTo(AwaitingPayment)
            .When<InventoryReservationFailed>()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        During(AwaitingPayment)
            .When<PaymentCharged>()
                .Publish((ctx, _) => new ShipOrder(ctx.CorrelationId, ctx.Saga.OrderId!))
                .TransitionTo(AwaitingShipment)
            .When<PaymentFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        During(AwaitingShipment)
            .When<OrderShipped>()
                .TransitionTo(Completed)
                .Finalize(SagaStatus.Completed)
            .When<ShipmentFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        ConfigureRecovery();
    }

    /// <summary>
    /// How a stalled or failed order unwinds. Split out of the constructor purely for length; it reads
    /// as one topic, and every rule here is about undoing work rather than driving the order forward.
    /// </summary>
    private void ConfigureRecovery()
    {
        Compensate(AwaitingInventory, (ctx, ct) => ctx.PublishAsync(new ReleaseInventory(ctx.CorrelationId, ctx.Saga.OrderId!), ct));
        Compensate(AwaitingPayment, (ctx, ct) => ctx.PublishAsync(new RefundPayment(ctx.CorrelationId, ctx.Saga.OrderId!), ct));

        // Every state that waits on a reply gets a timeout. Each is the same shape —
        // Compensate().TransitionTo(Failed).Finalize(TimedOut) — but unwinds a different amount of
        // work, because Compensate() walks the states this instance actually visited, most-recent
        // first, and runs whichever have a Compensate(state, ...) registered above:
        //
        //   AwaitingInventory — releases the hold that ReserveInventory may or may not have taken.
        //   AwaitingPayment   — releases the hold (InventoryReserved definitely succeeded to get here)
        //                       and defensively refunds, in case a hung gateway charged before going
        //                       quiet; the same pair the PaymentFailed branch compensates.
        //   AwaitingShipment  — refunds and releases, identical to the ShipmentFailed branch, since
        //                       both inventory and payment succeeded before the carrier went silent.
        //
        // A timeout here always means "no reply arrived in time", never "the participant declined" —
        // a decline is a real reply and takes the explicit ...Failed branch instead. So compensation is
        // necessarily defensive: the request may well have succeeded with only its reply lost, so the
        // compensating messages must be safe to receive for work that never happened. That is a real
        // constraint this sample places on its participants, not an incidental detail.
        foreach (var awaitingReply in new[] { AwaitingInventory, AwaitingPayment, AwaitingShipment })
        {
            WithTimeout(awaitingReply, ReplyTimeout,
                t => t.Compensate().TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
        }
    }
}
