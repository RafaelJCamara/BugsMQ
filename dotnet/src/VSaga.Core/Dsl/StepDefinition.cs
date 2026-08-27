using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

/// <summary>What to do when a specific message type arrives while the saga is in a specific state (or on a state's timeout).</summary>
internal sealed class StepDefinition<TState> where TState : SagaState, new()
{
    public Type MessageType { get; }

    public List<Func<ISagaContext<TState>, object, Task>> Actions { get; } = [];

    public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.None;

    public string? TargetStateName { get; set; }

    /// <summary>
    /// Set instead of <see cref="TargetStateName"/> when where this step goes next depends on the
    /// saga's accumulated state rather than on the step alone — an orchestrated fan-out/join, where
    /// each branch's reply either leaves the saga gathering or, if it was the last one outstanding,
    /// releases it to the next state.
    /// </summary>
    public Func<TState, string>? TargetStateSelector { get; set; }

    /// <summary>
    /// The single place the fixed and computed forms are collapsed, so the two DSLs can't drift on
    /// which wins — the same reason <see cref="ResolveFinalStatus"/>, <see cref="StepExecutor"/>, and
    /// <see cref="CompensationRunner"/> are shared. Evaluated after the step's actions have run, so a
    /// selector sees what they just wrote. Falls back to <paramref name="fromState"/> so a step that
    /// declares no transition at all stays put, which is the long-standing behaviour.
    /// </summary>
    public string ResolveTargetState(TState state, string fromState) =>
        TargetStateSelector is not null ? TargetStateSelector(state) : TargetStateName ?? fromState;

    public SagaStatus? FinalStatus { get; set; }

    /// <summary>
    /// Set instead of <see cref="FinalStatus"/> when whether this step is terminal depends on the saga's
    /// accumulated state rather than on the step alone — a choreography's fan-out/join, where the last of
    /// several independently-published events to arrive is the one that completes the saga and no single
    /// event type is reliably "last". Returning null means "handled, but not terminal yet".
    /// </summary>
    public Func<TState, SagaStatus?>? FinalStatusSelector { get; set; }

    /// <summary>
    /// The single place the two forms are collapsed, so <see cref="OrchestratedSagaDefinition{TState}"/>
    /// and <see cref="ChoreographedSagaDefinition{TState}"/> can't drift on which one wins — the same
    /// reason <see cref="StepExecutor"/> and <see cref="CompensationRunner"/> are shared. A selector, when
    /// present, is authoritative: the builders clear one when the other is set, so both are never
    /// configured at once.
    /// </summary>
    public SagaStatus? ResolveFinalStatus(TState state) =>
        FinalStatusSelector is not null ? FinalStatusSelector(state) : FinalStatus;

    public StepDefinition(Type messageType) => MessageType = messageType;
}

/// <summary>Marker CLR type used as the synthetic "message" passed to a state's timeout step.</summary>
internal sealed class TimeoutSignal
{
    public static readonly TimeoutSignal Instance = new();
}
