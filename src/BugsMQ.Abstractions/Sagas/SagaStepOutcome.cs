namespace BugsMQ.Abstractions.Sagas;

/// <summary>Result of running <see cref="ISagaDefinition{TState}.HandleAsync"/> for one message.</summary>
public readonly record struct SagaStepOutcome(bool WasHandled, string FromState, string ToState, SagaStatus? FinalStatus)
{
    /// <summary>No step was registered for the message's type in the saga's current state — the orchestrator logs it as an unexpected event and leaves state untouched.</summary>
    public static SagaStepOutcome Unhandled(string state) => new(false, state, state, null);
}
