using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Core.Dsl;

/// <summary>What to do when a specific message type arrives while the saga is in a specific state (or on a state's timeout).</summary>
internal sealed class StepDefinition<TState> where TState : SagaState, new()
{
    public Type MessageType { get; }

    public List<Func<ISagaContext<TState>, object, Task>> Actions { get; } = [];

    public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.None;

    public string? TargetStateName { get; set; }

    public SagaStatus? FinalStatus { get; set; }

    public StepDefinition(Type messageType) => MessageType = messageType;
}

/// <summary>Marker CLR type used as the synthetic "message" passed to a state's timeout step.</summary>
internal sealed class TimeoutSignal
{
    public static readonly TimeoutSignal Instance = new();
}
