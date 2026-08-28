using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Http.Tests;

public sealed record BeginHttpCall(string RequestId);

/// <summary>2xx loopback result.</summary>
public sealed record HttpCallSucceeded(string Body);

/// <summary>Explicit-status (402) loopback result.</summary>
public sealed record HttpCallDeclined(string Reason);

/// <summary>
/// Everything-else loopback result: a 5xx with a real body, or a network-level failure/timeout with
/// none at all (deserialized from an empty JSON object in that case) -- see
/// HttpCallBuilder.OnFailure&lt;TOut&gt;()'s own remarks on why every property here needs a default.
/// </summary>
public sealed record HttpCallFailed(string? Reason = null);

public sealed class CallHttpTestState : SagaState
{
    public string? Result { get; set; }
}

/// <summary>
/// One .CallHttp step covering the full mapping table from docs/design/http-based-sagas.md §5.4: a 2xx, an
/// explicit status (402), and everything else (5xx or a network-level failure) each loop back as their
/// own distinct message and drive this saga to a distinct terminal state, so a test can tell which
/// mapping actually fired by asserting the final state alone. The target URL is never actually dialled
/// -- every test wires a stub HttpMessageHandler in (see StubHttpMessageHandler), so what's configured
/// here only needs to be a well-formed absolute URI.
/// </summary>
public sealed class CallHttpTestSaga : OrchestratedSagaDefinition<CallHttpTestState>
{
    public State<CallHttpTestState> Start { get; }
    public State<CallHttpTestState> AwaitingResult { get; }
    public State<CallHttpTestState> Succeeded { get; }
    public State<CallHttpTestState> Declined { get; }
    public State<CallHttpTestState> Failed { get; }

    public CallHttpTestSaga()
    {
        Start = InitialState(nameof(Start));
        AwaitingResult = State(nameof(AwaitingResult));
        Succeeded = State(nameof(Succeeded));
        Declined = State(nameof(Declined));
        Failed = State(nameof(Failed));

        During(Start)
            .When<BeginHttpCall>()
                .CallHttp(h => h
                    .Post("https://call-target.test/charge")
                    .Body((ctx, m) => new { m.RequestId })
                    .OnSuccess<HttpCallSucceeded>()
                    .OnStatus(402).As<HttpCallDeclined>()
                    .OnFailure<HttpCallFailed>())
                .TransitionTo(AwaitingResult);

        During(AwaitingResult)
            .When<HttpCallSucceeded>()
                .Then((ctx, m) => ctx.Saga.Result = m.Body)
                .TransitionTo(Succeeded)
                .Finalize(SagaStatus.Completed)
            .When<HttpCallDeclined>()
                .Then((ctx, m) => ctx.Saga.Result = m.Reason)
                .TransitionTo(Declined)
                .Finalize(SagaStatus.Failed)
            .When<HttpCallFailed>()
                .Then((ctx, m) => ctx.Saga.Result = m.Reason)
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);
    }
}
