namespace BugsMQ.Core.Runtime;

/// <summary>Tunables for <see cref="SagaOrchestrator{TState}"/>'s infrastructure-failure handling.</summary>
public sealed class SagaOrchestratorOptions
{
    /// <summary>
    /// How many times an infrastructure-level failure — a deserialize error, a persistence-store
    /// exception, anything outside the saga definition's own step logic (which
    /// <see cref="SagaOrchestrator{TState}"/>'s HandleStepFailureAsync already handles by marking the
    /// saga Failed) — redelivers the same message before it's dead-lettered instead of requeued
    /// forever.
    /// </summary>
    public int MaxDeliveryAttempts { get; set; } = 5;
}
