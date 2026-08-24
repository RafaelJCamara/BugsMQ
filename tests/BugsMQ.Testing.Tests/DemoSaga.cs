using BugsMQ.Abstractions.Sagas;
using BugsMQ.Core.Dsl;

namespace BugsMQ.Testing.Tests;

public sealed class DemoSagaState : SagaState
{
    public string? OrderId { get; set; }
}

public sealed record OrderPlaced(string OrderId);
// Empty records are intentional: correlation is carried by the message envelope in tests, so these
// zero-payload markers just need a distinct CLR type for routing/deserialization.
#pragma warning disable S2094
public sealed record ShipmentConfirmed;
public sealed record ShipmentFailed;
#pragma warning restore S2094
public sealed record ReleaseHold(Guid CorrelationId);

public sealed class DemoSaga : OrchestratedSagaDefinition<DemoSagaState>
{
    public State<DemoSagaState> Placed { get; }
    public State<DemoSagaState> AwaitingShipment { get; }
    public State<DemoSagaState> Shipped { get; }
    public State<DemoSagaState> Failed { get; }

    public DemoSaga()
    {
        Placed = InitialState(nameof(Placed));
        AwaitingShipment = State(nameof(AwaitingShipment));
        Shipped = State(nameof(Shipped));
        Failed = State(nameof(Failed));

        During(Placed)
            .When<OrderPlaced>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .TransitionTo(AwaitingShipment);

        During(AwaitingShipment)
            .When<ShipmentConfirmed>()
                .TransitionTo(Shipped)
                .Finalize(SagaStatus.Completed)
            .When<ShipmentFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        Compensate(AwaitingShipment, (ctx, ct) => ctx.PublishAsync(new ReleaseHold(ctx.CorrelationId), ct));

        WithTimeout(AwaitingShipment, TimeSpan.FromMinutes(30), t => t.TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
    }
}
