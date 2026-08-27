using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

// Slice 2b: the engine, not saga code, publishes ChildSagaFinished to a child's parent when the child
// goes terminal through one of the two paths ctx.NotifyParentAsync structurally cannot reach — an
// unhandled exception, or a timeout. A parent only receives it if it declares a handler for it
// somewhere in its own DSL, which is what subscribes it — no separate opt-in switch exists.

public sealed record BeginSafeguardedJob(string JobId);
public sealed record BeginSafeguardedSlowJob(string JobId);
public sealed record BeginSafeguardedSuccessJob(string JobId);

public sealed class TestChildSafetyNetParentState : SagaState
{
    public string? JobId { get; set; }

    /// <summary>Set from ChildSagaFinished.Status once the safety net fires — null while waiting.</summary>
    public SagaStatus? ChildFinishedStatus { get; set; }
}

/// <summary>
/// A parent that starts a child and parks, having declared a handler for ChildSagaFinished — the
/// declaration itself is what opts it in (docs/sub-saga-composition.md's Slice 2b design). Two states for
/// the same reason every other awaiting-state sample in this repo uses two: the orchestrator only
/// schedules a timeout on a real transition.
/// </summary>
public sealed class TestChildSafetyNetParentSaga : OrchestratedSagaDefinition<TestChildSafetyNetParentState>
{
    public State<TestChildSafetyNetParentState> Requested { get; }
    public State<TestChildSafetyNetParentState> AwaitingResult { get; }
    public State<TestChildSafetyNetParentState> Rescued { get; }
    public State<TestChildSafetyNetParentState> Abandoned { get; }

    public TestChildSafetyNetParentSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingResult = State(nameof(AwaitingResult));
        Rescued = State(nameof(Rescued));
        Abandoned = State(nameof(Abandoned));

        During(Requested)
            .When<BeginSafeguardedJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new BeginRiskyWork(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult)
            // Same shape, but starts a child that only a timeout can ever move — see TestSlowChildSaga.
            .When<BeginSafeguardedSlowJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new BeginSlowWork(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult)
            // Same shape, but starts a child that finishes normally — see TestSucceedingChildSaga and
            // ChildSagaFinishedTests' scope-boundary test.
            .When<BeginSafeguardedSuccessJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new BeginSuccessWork(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<ChildSagaFinished>()
                .Then((ctx, m) => ctx.Saga.ChildFinishedStatus = m.Status)
                .TransitionTo(Rescued)
                .Finalize(SagaStatus.Failed);

        // Load-bearing backstop, not exercised by the tests that use this saga: proves the safety net,
        // not this timeout, is what actually released the parent in those tests.
        WithTimeout(AwaitingResult, TimeSpan.FromMinutes(5), t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}

public sealed record BeginRiskyWork(string JobId);

/// <summary>Published as a second, independent message so the failure below does not happen in the same call stack StartChildAsync used — see TestRacyFailureParentSaga for the deliberately-naive counterpart that does.</summary>
public sealed record TriggerFailure(string JobId);

public sealed class TestRiskyChildState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>
/// A child with genuine (simulated) work between being started and failing: BeginRiskyWork only records
/// the job id, and a later, separately-published TriggerFailure is what actually throws. Mirrors
/// NotifyParentAsyncFixtures' TestReportingChildSaga split for the same reason — every real child in this
/// repo has a genuine round-trip between StartChildAsync and whatever ends it.
/// </summary>
public sealed class TestRiskyChildSaga : OrchestratedSagaDefinition<TestRiskyChildState>
{
    public State<TestRiskyChildState> AwaitingWork { get; }
    public State<TestRiskyChildState> AwaitingTrigger { get; }

    public TestRiskyChildSaga()
    {
        AwaitingWork = InitialState(nameof(AwaitingWork));
        AwaitingTrigger = State(nameof(AwaitingTrigger));

        During(AwaitingWork)
            .When<BeginRiskyWork>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.Saga.JobId = m.JobId)
                .TransitionTo(AwaitingTrigger);

        During(AwaitingTrigger)
            .When<TriggerFailure>()
                .Then((ctx, _) => throw new InvalidOperationException("Simulated unhandled failure for ChildSagaFinished coverage."));
    }
}

public sealed record BeginSlowWork(string JobId);

public sealed class TestSlowChildState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>
/// A child that never reaches any step at all after starting — no handler is registered for anything in
/// Waiting, so only WithTimeout can ever move it. Models InvoiceArchivalSaga's own shape: the timeout
/// path deliberately never calls NotifyParentAsync, so ChildSagaFinished is the only thing that can ever
/// tell this child's parent it gave up.
/// </summary>
public sealed class TestSlowChildSaga : OrchestratedSagaDefinition<TestSlowChildState>
{
    public State<TestSlowChildState> AwaitingWork { get; }
    public State<TestSlowChildState> Waiting { get; }
    public State<TestSlowChildState> GaveUp { get; }

    public TestSlowChildSaga()
    {
        AwaitingWork = InitialState(nameof(AwaitingWork));
        Waiting = State(nameof(Waiting));
        GaveUp = State(nameof(GaveUp));

        During(AwaitingWork)
            .When<BeginSlowWork>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.Saga.JobId = m.JobId)
                .TransitionTo(Waiting);

        WithTimeout(Waiting, TimeSpan.FromMinutes(5), t => t.TransitionTo(GaveUp).Finalize(SagaStatus.TimedOut));
    }
}

public sealed record BeginSuccessWork(string JobId);
public sealed record CompleteWork(string JobId);

public sealed class TestSucceedingChildState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>
/// A child that finishes normally, through the ordinary message-driven success path
/// (HandleStepSuccessAsync) — and, deliberately, never calls ctx.NotifyParentAsync either. Exists purely
/// to pin the scope boundary: ChildSagaFinished must not fire here even though this saga does have a
/// parent and does go terminal, because that path is not one of the two structural gaps it exists to
/// cover — see ChildSagaFinishedTests' scope-boundary test.
/// </summary>
public sealed class TestSucceedingChildSaga : OrchestratedSagaDefinition<TestSucceedingChildState>
{
    public State<TestSucceedingChildState> AwaitingWork { get; }
    public State<TestSucceedingChildState> AwaitingCompletion { get; }
    public State<TestSucceedingChildState> Done { get; }

    public TestSucceedingChildSaga()
    {
        AwaitingWork = InitialState(nameof(AwaitingWork));
        AwaitingCompletion = State(nameof(AwaitingCompletion));
        Done = State(nameof(Done));

        During(AwaitingWork)
            .When<BeginSuccessWork>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.Saga.JobId = m.JobId)
                .TransitionTo(AwaitingCompletion);

        During(AwaitingCompletion)
            .When<CompleteWork>()
                .TransitionTo(Done)
                .Finalize(SagaStatus.Completed);
    }
}

public sealed record BeginRacyFailureJob(string JobId);
public sealed record BeginRacyFailure(string JobId);

public sealed class TestRacyFailureParentState : SagaState
{
    public string? JobId { get; set; }

    public SagaStatus? ChildFinishedStatus { get; set; }
}

/// <summary>Identical shape to TestChildSafetyNetParentSaga, kept separate so the two tests don't share engine state, and paired with TestImmediatelyFailingChildSaga instead.</summary>
public sealed class TestRacyFailureParentSaga : OrchestratedSagaDefinition<TestRacyFailureParentState>
{
    public State<TestRacyFailureParentState> Requested { get; }
    public State<TestRacyFailureParentState> AwaitingResult { get; }
    public State<TestRacyFailureParentState> Rescued { get; }

    public TestRacyFailureParentSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingResult = State(nameof(AwaitingResult));
        Rescued = State(nameof(Rescued));

        During(Requested)
            .When<BeginRacyFailureJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new BeginRacyFailure(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<ChildSagaFinished>()
                .Then((ctx, m) => ctx.Saga.ChildFinishedStatus = m.Status)
                .TransitionTo(Rescued)
                .Finalize(SagaStatus.Failed);

        WithTimeout(AwaitingResult, TimeSpan.FromMinutes(5), t => t.Finalize(SagaStatus.TimedOut));
    }
}

public sealed class TestImmediatelyFailingChildState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>
/// The narrow, deliberately naive counterpart to <see cref="TestRiskyChildSaga"/>: fails in the very same
/// step BeginRacyFailure starts it in, with no intervening work at all. Exists only to pin the
/// StepFailed-path analogue of the race NotifyParentAsyncFixtures.TestImmediatelyReportingChildSaga pins
/// for the NotifyParentAsync path — see
/// <see cref="ChildSagaFinishedTests.ChildSagaFinished_FromAChildsOwnInitiatingStep_CanRaceAheadOfTheParentsUnpersistedTransition"/>.
/// </summary>
public sealed class TestImmediatelyFailingChildSaga : OrchestratedSagaDefinition<TestImmediatelyFailingChildState>
{
    public State<TestImmediatelyFailingChildState> Failing { get; }

    public TestImmediatelyFailingChildSaga()
    {
        Failing = InitialState(nameof(Failing));

        During(Failing)
            .When<BeginRacyFailure>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, _) => throw new InvalidOperationException("Simulated immediate unhandled failure."));
    }
}

// A separate, isolated saga pair whose parent never declares a handler for ChildSagaFinished at all —
// used to prove the "opt-in" is really just the existing declared-handler subscription mechanism, not a
// new switch. Kept in its own DI container (ChildSagaFinishedOptInTests) rather than sharing the one
// above, since registering ANY saga type with a ChildSagaFinished handler in a container makes the
// engine subscribe to that message type for that saga type — which would let this scenario's target
// receive it via a different queue than the one being tested, muddying the proof.

public sealed record BeginUnsafeguardedJob(string JobId);

public sealed class TestNaiveParentState : SagaState
{
    public string? JobId { get; set; }
}

/// <summary>Starts a risky child and parks, but never declares a handler for ChildSagaFinished anywhere — so it never subscribes to it, and never receives it, regardless of what the engine publishes.</summary>
public sealed class TestNaiveParentSaga : OrchestratedSagaDefinition<TestNaiveParentState>
{
    public State<TestNaiveParentState> Requested { get; }
    public State<TestNaiveParentState> AwaitingResult { get; }
    public State<TestNaiveParentState> Abandoned { get; }

    public TestNaiveParentSaga()
    {
        Requested = InitialState(nameof(Requested));
        AwaitingResult = State(nameof(AwaitingResult));
        Abandoned = State(nameof(Abandoned));

        During(Requested)
            .When<BeginUnsafeguardedJob>()
                .CorrelateBy(m => m.JobId, s => s.JobId)
                .Then((ctx, m) => ctx.StartChildAsync(new BeginRiskyWork(m.JobId), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        WithTimeout(AwaitingResult, TimeSpan.FromMinutes(5), t => t.TransitionTo(Abandoned).Finalize(SagaStatus.TimedOut));
    }
}
