using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

public sealed record FanOutTriggered(string OrderId);
public sealed record BranchAReported(string OrderId);
public sealed record BranchBReported(string OrderId);
public sealed record BranchCReported(string OrderId);

public sealed class FanOutState : SagaState
{
    public string? OrderId { get; set; }

    public bool A { get; set; }

    public bool B { get; set; }

    public bool C { get; set; }
}

/// <summary>
/// The fan-out/join shape the OrderProcessing sample's <c>PostShipmentChoreography</c> uses, reduced to
/// the bare mechanics: one trigger event opens the instance, three independent branches each report in
/// once, and the saga completes when the last of them lands — whichever that turns out to be.
///
/// <para>
/// This is what motivated <c>ChoreographyEventBuilder.Finalize(Func&lt;TState, SagaStatus?&gt;)</c>. The
/// fixed-status <c>Finalize(SagaStatus)</c> cannot express it: nominating one branch as the finisher is
/// wrong when the three have no fixed order, and nominating none means the saga never completes.
/// </para>
/// </summary>
public sealed class TestFanOutChoreography : ChoreographedSagaDefinition<FanOutState>
{
    public State<FanOutState> Awaiting { get; }
    public State<FanOutState> SawA { get; }
    public State<FanOutState> SawB { get; }
    public State<FanOutState> SawC { get; }

    public TestFanOutChoreography()
    {
        Awaiting = InitialState(nameof(Awaiting));
        SawA = State(nameof(SawA));
        SawB = State(nameof(SawB));
        SawC = State(nameof(SawC));

        On<FanOutTriggered>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .RecordState(Awaiting);

        // Every branch can also open the instance: an independent participant may report before the
        // tracker has processed the trigger itself.
        On<BranchAReported>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.A = true)
            .RecordState(SawA)
            .Finalize(CompleteWhenAllReported);

        On<BranchBReported>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.B = true)
            .RecordState(SawB)
            .Finalize(CompleteWhenAllReported);

        On<BranchCReported>()
            .StartsNewInstance()
            .CorrelateBy(m => m.OrderId, s => s.OrderId)
            .Then((ctx, _) => ctx.Saga.C = true)
            .RecordState(SawC)
            .Finalize(CompleteWhenAllReported);
    }

    private static SagaStatus? CompleteWhenAllReported(FanOutState s) =>
        s.A && s.B && s.C ? SagaStatus.Completed : null;
}
