using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

public sealed record ParallelOrderPlaced(string OrderId);

// The three commands the fan-out dispatches at once, and the replies that come back independently.
public sealed record ReserveStock(Guid CorrelationId, string OrderId);
public sealed record AuthorizePayment(Guid CorrelationId, string OrderId);
public sealed record RunFraudCheck(Guid CorrelationId, string OrderId);

public sealed record StockReserved(string OrderId);
public sealed record PaymentAuthorized(string OrderId);
public sealed record FraudCheckCleared(string OrderId);

public sealed class ParallelFulfilmentState : SagaState
{
    public string? OrderId { get; set; }

    public bool StockReserved { get; set; }

    public bool PaymentAuthorized { get; set; }

    public bool FraudCleared { get; set; }

    public bool AllBranchesReady => StockReserved && PaymentAuthorized && FraudCleared;
}

/// <summary>
/// An orchestrated fan-out/join: one step dispatches three commands at once, then the saga gathers
/// their replies and only advances when the last one lands.
///
/// <para>
/// The fan-out half needed no new DSL — <c>.Publish(...)</c> chains, so a single step can dispatch
/// several commands. The join half is <c>TransitionTo(selector)</c>: each branch either leaves the
/// saga in <c>Gathering</c> (a self-transition) or, if it was the last outstanding one, releases it to
/// <c>ReadyToShip</c>. Registering the same selector on all three means none of them has to know
/// whether it is last.
/// </para>
/// </summary>
public sealed class TestParallelFulfilmentSaga : OrchestratedSagaDefinition<ParallelFulfilmentState>
{
    public State<ParallelFulfilmentState> Placed { get; }
    public State<ParallelFulfilmentState> Gathering { get; }
    public State<ParallelFulfilmentState> ReadyToShip { get; }
    public State<ParallelFulfilmentState> Abandoned { get; }

    public TestParallelFulfilmentSaga()
    {
        Placed = InitialState(nameof(Placed));
        Gathering = State(nameof(Gathering));
        ReadyToShip = State(nameof(ReadyToShip));
        Abandoned = State(nameof(Abandoned));

        // Three commands dispatched from one step — the fan-out. Nothing here waits on anything.
        During(Placed)
            .When<ParallelOrderPlaced>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Publish((ctx, m) => new ReserveStock(ctx.CorrelationId, m.OrderId))
                .Publish((ctx, m) => new AuthorizePayment(ctx.CorrelationId, m.OrderId))
                .Publish((ctx, m) => new RunFraudCheck(ctx.CorrelationId, m.OrderId))
                .TransitionTo(Gathering);

        During(Gathering)
            .When<StockReserved>()
                .Then((ctx, _) => ctx.Saga.StockReserved = true)
                .TransitionTo(ReleaseWhenAllReady)
            .When<PaymentAuthorized>()
                .Then((ctx, _) => ctx.Saga.PaymentAuthorized = true)
                .TransitionTo(ReleaseWhenAllReady)
            .When<FraudCheckCleared>()
                .Then((ctx, _) => ctx.Saga.FraudCleared = true)
                .TransitionTo(ReleaseWhenAllReady);

        // One deadline covers the whole gather: returning Gathering is a self-transition, which the
        // orchestrator treats as "no transition" and so neither cancels nor reschedules this timeout.
        // An arriving branch therefore does not silently extend the deadline.
        WithTimeout(Gathering, TimeSpan.FromMinutes(5),
            t => t.Compensate().TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }

    /// <summary>
    /// The join condition, registered identically on all three branches and evaluated after the
    /// branch's own action has run, so the flag it just set counts.
    /// </summary>
    private State<ParallelFulfilmentState> ReleaseWhenAllReady(ParallelFulfilmentState state) =>
        state.AllBranchesReady ? ReadyToShip : Gathering;
}

/// <summary>A structural copy of <see cref="ParallelFulfilmentState"/>: each saga definition needs its
/// own state class, since the engine resolves definitions by state type.</summary>
public sealed class TerminalJoinState : SagaState
{
    public string? OrderId { get; set; }

    public bool StockReserved { get; set; }

    public bool PaymentAuthorized { get; set; }

    public bool FraudCleared { get; set; }

    public bool AllBranchesReady => StockReserved && PaymentAuthorized && FraudCleared;
}

/// <summary>
/// The same fan-out, but the join is the saga's ending rather than a step on the way to one — the case
/// that needs <c>Finalize(selector)</c>, since the branch that completes the saga is whichever happens
/// to arrive last.
/// </summary>
public sealed class TestTerminalJoinSaga : OrchestratedSagaDefinition<TerminalJoinState>
{
    public State<TerminalJoinState> Placed { get; }
    public State<TerminalJoinState> Gathering { get; }
    public State<TerminalJoinState> Done { get; }

    public TestTerminalJoinSaga()
    {
        Placed = InitialState(nameof(Placed));
        Gathering = State(nameof(Gathering));
        Done = State(nameof(Done));

        During(Placed)
            .When<ParallelOrderPlaced>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .TransitionTo(Gathering);

        During(Gathering)
            .When<StockReserved>()
                .Then((ctx, _) => ctx.Saga.StockReserved = true)
                .TransitionTo(FinishWhenAllReady)
                .Finalize(CompleteWhenAllReady)
            .When<PaymentAuthorized>()
                .Then((ctx, _) => ctx.Saga.PaymentAuthorized = true)
                .TransitionTo(FinishWhenAllReady)
                .Finalize(CompleteWhenAllReady)
            .When<FraudCheckCleared>()
                .Then((ctx, _) => ctx.Saga.FraudCleared = true)
                .TransitionTo(FinishWhenAllReady)
                .Finalize(CompleteWhenAllReady);
    }

    private State<TerminalJoinState> FinishWhenAllReady(TerminalJoinState state) =>
        state.AllBranchesReady ? Done : Gathering;

    private static SagaStatus? CompleteWhenAllReady(TerminalJoinState state) =>
        state.AllBranchesReady ? SagaStatus.Completed : null;
}
