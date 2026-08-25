namespace BugsMQ.Abstractions.Sagas;

/// <summary>
/// Engine-published safety net for the two cases a child cannot report through
/// <c>ISagaContext.NotifyParentAsync</c> because it never reaches a step that could call it: an
/// unhandled exception, or a timeout that goes terminal. Published by the child's own
/// <c>SagaOrchestrator</c> directly — not through <c>ISagaContext</c> — under
/// <c>Saga.ParentCorrelationId</c>, the same target <c>NotifyParentAsync</c> addresses.
/// <para>
/// Carries no domain result, only the fact and the terminal status: a parent that needs to know what a
/// child actually did (not just that it stopped) still needs the child's own
/// <c>NotifyParentAsync</c>-carried message for its success path. A parent receives this only if it has
/// declared a handler for it somewhere in its own DSL — that declaration is what subscribes it; there is
/// no separate opt-in switch.
/// </para>
/// </summary>
public sealed record ChildSagaFinished(Guid ChildCorrelationId, string ChildSagaType, SagaStatus Status);
