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

        Compensate(AwaitingInventory, (ctx, ct) => ctx.PublishAsync(new ReleaseInventory(ctx.CorrelationId, ctx.Saga.OrderId!), ct));
        Compensate(AwaitingPayment, (ctx, ct) => ctx.PublishAsync(new RefundPayment(ctx.CorrelationId, ctx.Saga.OrderId!), ct));

        WithTimeout(AwaitingPayment, TimeSpan.FromSeconds(30),
            t => t.TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
    }
}
