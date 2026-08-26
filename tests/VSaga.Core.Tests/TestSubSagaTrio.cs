using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

// A three-level chain — fulfilment starts a delivery, which starts an archival — so tests can pin
// that FindChildrenAsync returns direct children only, and that linkage survives a second hop rather
// than happening to work for the first.
public sealed record BeginFulfilment(string OrderId);
public sealed record BeginOrphanFulfilment(string OrderId);
public sealed record DeliverParcel(string OrderId);
public sealed record ArchiveProofOfDelivery(string OrderId);

/// <summary>Published as a child's initiating message, but no registered saga initiates on it — failure mode "the child never starts".</summary>
public sealed record NobodyInitiatesOnThis(string OrderId);

public sealed class TestFulfilmentState : SagaState
{
    public string? OrderId { get; set; }
}

public sealed class TestParcelState : SagaState
{
    public string? OrderId { get; set; }
}

public sealed class TestArchiveState : SagaState
{
    public string? OrderId { get; set; }
}

/// <summary>
/// Root of the chain. Starts a sub-saga and parks — it does not wait for it, because Slice 1 has no
/// way for a child to address its parent (a child's <c>PublishAsync</c> goes out under the child's own
/// correlation id). The parking state carries a timeout for the same reason every other awaiting state
/// in this repo does.
/// </summary>
public sealed class TestFulfilmentSaga : OrchestratedSagaDefinition<TestFulfilmentState>
{
    public State<TestFulfilmentState> Requested { get; }
    public State<TestFulfilmentState> AwaitingChild { get; }
    public State<TestFulfilmentState> Abandoned { get; }

    public TestFulfilmentSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingChild = State(nameof(AwaitingChild));
        Abandoned = State(nameof(Abandoned));

        During(Requested)
            .When<BeginFulfilment>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.StartChildAsync(new DeliverParcel(m.OrderId), ctx.CancellationToken))
                .TransitionTo(AwaitingChild)
            // Same call, but with a message type no registered saga initiates on. Nothing about the
            // publish distinguishes the two cases — which is the point of the test that uses this.
            .When<BeginOrphanFulfilment>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.StartChildAsync(new NobodyInitiatesOnThis(m.OrderId), ctx.CancellationToken))
                .TransitionTo(AwaitingChild);

        WithTimeout(AwaitingChild, TimeSpan.FromMinutes(5), t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}

/// <summary>Middle of the chain: a child that is itself a parent.</summary>
public sealed class TestParcelSaga : OrchestratedSagaDefinition<TestParcelState>
{
    public State<TestParcelState> Accepted { get; }
    public State<TestParcelState> Archiving { get; }

    public TestParcelSaga()
    {
        Accepted = InitialState(nameof(Accepted));
        Archiving = State(nameof(Archiving));

        During(Accepted)
            .When<DeliverParcel>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.StartChildAsync(new ArchiveProofOfDelivery(m.OrderId), ctx.CancellationToken))
                .TransitionTo(Archiving);
    }
}

/// <summary>Leaf of the chain.</summary>
public sealed class TestArchiveSaga : OrchestratedSagaDefinition<TestArchiveState>
{
    public State<TestArchiveState> Filed { get; }
    public State<TestArchiveState> Stored { get; }

    public TestArchiveSaga()
    {
        Filed = InitialState(nameof(Filed));
        Stored = State(nameof(Stored));

        During(Filed)
            .When<ArchiveProofOfDelivery>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .TransitionTo(Stored)
                .Finalize(SagaStatus.Completed);
    }
}
