namespace BugsMQ.Abstractions.Sagas;

/// <summary>
/// Describes a saga: its identity, the message types it reacts to, and how it reacts to them.
/// Implemented by the fluent DSL base classes in BugsMQ.Core; consumed by the orchestrator/engine
/// and by anything (e.g. the dashboard) that needs to introspect saga metadata without depending on Core.
/// </summary>
public interface ISagaDefinition<TState> where TState : SagaState, new()
{
    string SagaType { get; }

    SagaKind Kind { get; }

    /// <summary>The state a brand new saga instance starts in.</summary>
    string InitialStateName { get; }

    /// <summary>All message CLR types this saga has a handler registered for, in any state.</summary>
    IReadOnlyCollection<Type> MessageTypes { get; }

    /// <summary>Message types that can start a brand new saga instance (registered under <see cref="InitialStateName"/>).</summary>
    IReadOnlyCollection<Type> InitiatingMessageTypes { get; }

    bool CanInitiate(Type messageType);

    /// <summary>
    /// Runs the registered handler (if any) for <paramref name="message"/> against the saga's current
    /// state. Mutates <paramref name="context"/>.Saga in place (CurrentState/Status/business fields)
    /// and may call context.PublishAsync/SendAsync as side effects. Throws if the step's action fails
    /// after any configured step-level retries are exhausted; the orchestrator is responsible for
    /// marking the saga Failed and persisting in that case.
    /// </summary>
    Task<SagaStepOutcome> HandleAsync(ISagaContext<TState> context, object message, CancellationToken cancellationToken);

    /// <summary>Runs the timeout handler registered for <paramref name="forState"/>, if any.</summary>
    Task<SagaStepOutcome> HandleTimeoutAsync(ISagaContext<TState> context, string forState, CancellationToken cancellationToken);

    /// <summary>Timeout duration configured for a state, if any (used to schedule a timeout on entry).</summary>
    TimeSpan? GetTimeout(string forState);
}
