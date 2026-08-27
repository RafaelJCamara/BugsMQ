using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

// A parent that starts a child and genuinely parks for its answer — the shape Slice 2a adds on top of
// Slice 1's TestFulfilmentSaga/TestParcelSaga, which parks but has no way to be released early. Two
// states on the parent (Requested, AwaitingResult) rather than one, for the same reason
// PostShipmentChoreography's own doc comment calls out: the orchestrator only schedules a timeout on a
// real transition, and folding the start-and-park into one self-transitioning state would schedule none.
public sealed record BeginJob(string JobId);
public sealed record BeginJobWithNoWorker(string JobId);
public sealed record ProcessItem(string JobId);

/// <summary>Published as a child's initiating message, but no registered saga initiates on it — same failure mode SubSagaCompositionTests pins for StartChildAsync.</summary>
public sealed record NobodyProcessesThis(string JobId);

/// <summary>Published by a child via ctx.NotifyParentAsync — carries the domain result, which is the whole point of (a) over an engine-published completion event.</summary>
public sealed record ItemProcessed(string JobId, bool Succeeded);

public sealed class TestWaitingParentState : SagaState
{
    public string? JobId { get; set; }

    public bool? ChildSucceeded { get; set; }
}

public sealed class TestWaitingParentSaga : OrchestratedSagaDefinition<TestWaitingParentState>
{
    public State<TestWaitingParentState> Requested { get; }
    public State<TestWaitingParentState> AwaitingResult { get; }
    public State<TestWaitingParentState> Done { get; }
    public State<TestWaitingParentState> Abandoned { get; }

    public TestWaitingParentSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingResult = State(nameof(AwaitingResult));
        Done = State(nameof(Done));
        Abandoned = State(nameof(Abandoned));

        During(Requested)
            .When<BeginJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new ProcessItem(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult)
            // Same call, but with a message type no registered saga initiates on — see NobodyProcessesThis.
            .When<BeginJobWithNoWorker>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new NobodyProcessesThis(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<ItemProcessed>()
                .Then((ctx, m) => ctx.Saga.ChildSucceeded = m.Succeeded)
                .TransitionTo(Done)
                .Finalize(SagaStatus.Completed);

        // Load-bearing for TheParentsOwnTimeoutStillCoversAChildThatNeverStarts: NotifyParentAsync is
        // the child's own step, not the engine's, so a child that never reaches it (crashes, or is
        // simply never started because nothing initiates on its message) leaves this the only rescue.
        WithTimeout(AwaitingResult, TimeSpan.FromMinutes(5), t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}

/// <summary>
/// Published by whatever does the child's real work — a stand-in for a participant's reply, published
/// by the test itself as a separate top-level message rather than from inside ProcessItem's own step.
/// </summary>
public sealed record ItemActuallyProcessed(string JobId);

public sealed class TestReportingChildState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>
/// The child: does its own thing, then addresses its parent directly rather than being told to by the
/// engine. Two states rather than one, and load-bearing rather than stylistic —
/// <see cref="NotifyParentAsyncTests.NotifyParentAsync_ReleasesTheParentsWaitWithTheChildsActualResult"/>
/// documents why: the in-memory transport dispatches a publish's subscribers synchronously and
/// recursively (see InMemoryMessageTransport.DispatchAsync), so a child that called NotifyParentAsync
/// from the very same step ProcessItem starts it in would still be nested inside the parent's own
/// StartChildAsync call, before the parent has persisted its AwaitingResult transition at all. Splitting
/// the child's own work onto a second, independently-published message is not just realism for its own
/// sake — every real child in this repo (InvoiceDeliverySaga, InvoiceArchivalSaga) already looks like
/// this, because NotifyParentAsync only has an answer to give once something real has happened.
/// </summary>
public sealed class TestReportingChildSaga : OrchestratedSagaDefinition<TestReportingChildState>
{
    public State<TestReportingChildState> AwaitingWork { get; }
    public State<TestReportingChildState> Reported { get; }

    public TestReportingChildSaga()
    {
        AwaitingWork = InitialState(nameof(AwaitingWork));
        Reported = State(nameof(Reported));

        During(AwaitingWork)
            // No TransitionTo: falls back to the fromState, so the instance stays in AwaitingWork until
            // ItemActuallyProcessed arrives as its own, separately-published message.
            .When<ProcessItem>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.Saga.JobId = m.JobId)
            .When<ItemActuallyProcessed>()
                .Then((ctx, _) => ctx.NotifyParentAsync(new ItemProcessed(ctx.Saga.JobId!, Succeeded: true), ctx.CancellationToken))
                .TransitionTo(Reported)
                .Finalize(SagaStatus.Completed);
    }
}

/// <summary>
/// The narrow, deliberately naive counterpart to <see cref="TestReportingChildSaga"/>: notifies its
/// parent from the very same step that <c>ProcessItem</c> starts it in, with no intervening work at all.
/// Exists only to pin the race documented on <see cref="TestReportingChildSaga"/> — see
/// <see cref="NotifyParentAsyncTests.NotifyParentAsync_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition"/>.
/// </summary>
public sealed record BeginRacyJob(string JobId);

public sealed class TestImmediatelyReportingChildState : SagaState
{
    public string? JobId { get; set; }
}

public sealed class TestImmediatelyReportingChildSaga : OrchestratedSagaDefinition<TestImmediatelyReportingChildState>
{
    public State<TestImmediatelyReportingChildState> Reported { get; }

    public TestImmediatelyReportingChildSaga()
    {
        Reported = InitialState(nameof(Reported));

        During(Reported)
            .When<RacyProcessItem>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.NotifyParentAsync(new ItemProcessed(m.JobId, Succeeded: true), ctx.CancellationToken))
                .Finalize(SagaStatus.Completed);
    }
}

public sealed record RacyProcessItem(string JobId);

public sealed class TestRacyParentState : SagaState
{
    public string? JobId { get; set; }

    public bool? ChildSucceeded { get; set; }
}

/// <summary>Identical shape to <see cref="TestWaitingParentSaga"/>, kept separate so the two tests don't share engine state, and paired with <see cref="TestImmediatelyReportingChildSaga"/> instead.</summary>
public sealed class TestRacyParentSaga : OrchestratedSagaDefinition<TestRacyParentState>
{
    public State<TestRacyParentState> Requested { get; }
    public State<TestRacyParentState> AwaitingResult { get; }
    public State<TestRacyParentState> Done { get; }

    public TestRacyParentSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingResult = State(nameof(AwaitingResult));
        Done = State(nameof(Done));

        During(Requested)
            .When<BeginRacyJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new RacyProcessItem(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<ItemProcessed>()
                .Then((ctx, m) => ctx.Saga.ChildSucceeded = m.Succeeded)
                .TransitionTo(Done)
                .Finalize(SagaStatus.Completed);

        WithTimeout(AwaitingResult, TimeSpan.FromMinutes(5), t => t.Finalize(SagaStatus.TimedOut));
    }
}

/// <summary>A root saga (no StartChildAsync ever ran) that nonetheless tries to notify a parent — exercises the fail-loudly guard.</summary>
public sealed record BeginOrphanJob(string JobId);

public sealed class TestOrphanState : SagaState
{
    public string? JobId { get; set; }
}

public sealed class TestOrphanSaga : OrchestratedSagaDefinition<TestOrphanState>
{
    public State<TestOrphanState> Requested { get; }
    public State<TestOrphanState> Reported { get; }

    public TestOrphanSaga()
    {
        Requested = InitialState(nameof(Requested));
        Reported = State(nameof(Reported));

        During(Requested)
            .When<BeginOrphanJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.NotifyParentAsync(new ItemProcessed(m.JobId, Succeeded: true), ctx.CancellationToken))
                .TransitionTo(Reported)
                .Finalize(SagaStatus.Completed);
    }
}
