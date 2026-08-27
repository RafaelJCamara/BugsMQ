using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Http.Tests;

public sealed record BeginHttpCallAsync(string RequestId);

/// <summary>2xx loopback result.</summary>
public sealed record HttpAsyncCallSucceeded(string Body);

/// <summary>Explicit-status (402) loopback result.</summary>
public sealed record HttpAsyncCallDeclined(string Reason);

/// <summary>Everything-else loopback result -- see HttpCallFailed's own remarks on why every property needs a default.</summary>
public sealed record HttpAsyncCallFailed(string? Reason = null);

public sealed class CallHttpAsyncTestState : SagaState
{
    public string? Result { get; set; }
}

/// <summary>
/// docs/mixed-sagas.md §4/§9's mapping-table mirror of <see cref="CallHttpTestSaga"/>, but reached via
/// <c>ctx.CallHttpAsync(...)</c> from an ordinary <c>.Then(Func&lt;...,Task&gt;)</c> step instead of the
/// declarative <c>.CallHttp(...)</c> -- proving the shared executor maps every outcome identically
/// regardless of which DSL entry point drives it.
/// </summary>
public sealed class CallHttpAsyncTestSaga : OrchestratedSagaDefinition<CallHttpAsyncTestState>
{
    public State<CallHttpAsyncTestState> Start { get; }
    public State<CallHttpAsyncTestState> AwaitingResult { get; }
    public State<CallHttpAsyncTestState> Succeeded { get; }
    public State<CallHttpAsyncTestState> Declined { get; }
    public State<CallHttpAsyncTestState> Failed { get; }

    public CallHttpAsyncTestSaga()
    {
        Start = InitialState(nameof(Start));
        AwaitingResult = State(nameof(AwaitingResult));
        Succeeded = State(nameof(Succeeded));
        Declined = State(nameof(Declined));
        Failed = State(nameof(Failed));

        During(Start)
            .When<BeginHttpCallAsync>()
                .Then((ctx, m) => ctx.CallHttpAsync(h => h
                    .Post("https://call-target.test/charge-async")
                    .Body(new { m.RequestId })
                    .OnSuccess<HttpAsyncCallSucceeded>()
                    .OnStatus(402).As<HttpAsyncCallDeclined>()
                    .OnFailure<HttpAsyncCallFailed>(), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<HttpAsyncCallSucceeded>()
                .Then((ctx, m) => ctx.Saga.Result = m.Body)
                .TransitionTo(Succeeded)
                .Finalize(SagaStatus.Completed)
            .When<HttpAsyncCallDeclined>()
                .Then((ctx, m) => ctx.Saga.Result = m.Reason)
                .TransitionTo(Declined)
                .Finalize(SagaStatus.Failed)
            .When<HttpAsyncCallFailed>()
                .Then((ctx, m) => ctx.Saga.Result = m.Reason)
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);
    }
}

/// <summary>
/// A payload whose serialized value changes every time it's actually serialized -- the only way to
/// observe from outside the DSL whether the shared executor's retry loop invokes <c>.Body(...)</c>'s
/// captured value fresh per attempt or computed/serialized it once and reused the bytes. Deliberately
/// never stored on saga state: SagaTestHarness's in-memory store round-trips state through JSON on every
/// read/write (real snapshot-isolation semantics, not object-reference sharing -- see
/// InMemorySagaStore's own doc comment), which would silently reset this counter. CallHttpAsyncRetryTests
/// instead observes it via the raw bytes each attempt actually put on the wire.
/// </summary>
public sealed class CountingPayload
{
    private int _timesSerialized;

    public int Attempt => ++_timesSerialized;
}
