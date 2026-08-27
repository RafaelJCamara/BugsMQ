using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Http.Tests;

public sealed record BeginRetryCall(string RequestId);

public sealed record RetryCallSucceeded(string Body);

public sealed class CallHttpAsyncRetryTestState : SagaState
{
    public string? Result { get; set; }
}

/// <summary>
/// docs/mixed-sagas.md §4/§9: pins that a retried <c>ctx.CallHttpAsync</c> call re-invokes its
/// <c>.Body(...)</c> value's serialization once per attempt, not once total -- see
/// <see cref="CountingPayload"/> and CallHttpAsyncRetryTests.
/// </summary>
public sealed class CallHttpAsyncRetryTestSaga : OrchestratedSagaDefinition<CallHttpAsyncRetryTestState>
{
    public State<CallHttpAsyncRetryTestState> Start { get; }
    public State<CallHttpAsyncRetryTestState> AwaitingResult { get; }
    public State<CallHttpAsyncRetryTestState> Succeeded { get; }

    public CallHttpAsyncRetryTestSaga()
    {
        Start = InitialState(nameof(Start));
        AwaitingResult = State(nameof(AwaitingResult));
        Succeeded = State(nameof(Succeeded));

        During(Start)
            .When<BeginRetryCall>()
                .Then((ctx, m) => ctx.CallHttpAsync(h => h
                    .Post("https://call-target.test/flaky")
                    .Body(new CountingPayload())
                    .WithRetry(maxAttempts: 3, delay: TimeSpan.FromMilliseconds(1))
                    .OnSuccess<RetryCallSucceeded>()
                    .OnFailure(s => s.Result = "failed"), ctx.CancellationToken))
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<RetryCallSucceeded>()
                .Then((ctx, m) => ctx.Saga.Result = m.Body)
                .TransitionTo(Succeeded)
                .Finalize(SagaStatus.Completed);
    }
}
