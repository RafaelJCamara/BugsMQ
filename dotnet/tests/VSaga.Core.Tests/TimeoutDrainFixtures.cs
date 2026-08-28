using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

public sealed record BeginDrainTest(string OrderId);

// Empty record is intentional -- see TestOrderSaga.cs's own precedent.
#pragma warning disable S2094
public sealed record DrainLoopbackAck;
#pragma warning restore S2094

public sealed class TimeoutDrainTestState : SagaState
{
    public string? OrderId { get; set; }
}

/// <summary>
/// docs/design/mixed-sagas.md §3.1/§5: a timeout step that queues a loopback (ctx.PublishAfterCommitAsync,
/// through the new async TimeoutBuilder.Then overload) and transitions onward to a state that handles
/// the reply. Before HandleTimeoutAsync's own drain, this loopback was silently dropped and the saga sat
/// in <see cref="Draining"/> forever -- see TimeoutDrainTests.
/// </summary>
public sealed class TimeoutDrainTestSaga : OrchestratedSagaDefinition<TimeoutDrainTestState>
{
    public State<TimeoutDrainTestState> Start { get; }
    public State<TimeoutDrainTestState> Waiting { get; }
    public State<TimeoutDrainTestState> Draining { get; }
    public State<TimeoutDrainTestState> Done { get; }

    public TimeoutDrainTestSaga()
    {
        Start = InitialState(nameof(Start));
        Waiting = State(nameof(Waiting));
        Draining = State(nameof(Draining));
        Done = State(nameof(Done));

        During(Start)
            .When<BeginDrainTest>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .TransitionTo(Waiting);

        During(Draining)
            .When<DrainLoopbackAck>()
                .TransitionTo(Done)
                .Finalize(SagaStatus.Completed);

        WithTimeout(Waiting, TimeSpan.FromMinutes(5), t => t
            .Then(ctx => ctx.PublishAfterCommitAsync(new DrainLoopbackAck(), ctx.CancellationToken))
            .TransitionTo(Draining));
    }
}
