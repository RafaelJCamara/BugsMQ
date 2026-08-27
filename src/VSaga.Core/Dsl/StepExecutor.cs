using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;

namespace VSaga.Core.Dsl;

/// <summary>
/// Runs one step's actions with its configured <see cref="RetryPolicy"/>, shared by both
/// <see cref="OrchestratedSagaDefinition{TState}"/> and <see cref="ChoreographedSagaDefinition{TState}"/>
/// so the two DSLs can't silently drift on retry/backoff semantics.
/// </summary>
internal static class StepExecutor
{
    public static async Task RunAsync<TState>(StepDefinition<TState> step, ISagaContext<TState> context, object message, CancellationToken cancellationToken)
        where TState : SagaState, new()
    {
        var attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                foreach (var action in step.Actions)
                    await action(context, message);

                return;
            }
            catch when (attempt < step.RetryPolicy.MaxAttempts)
            {
                // docs/mixed-sagas.md §3.2: a replay from index 0 re-runs every action, including any
                // ctx.PublishAfterCommitAsync call that already queued before the throw -- each mints a
                // fresh MessageId, so without this the drain would publish one copy per attempt and
                // IsDuplicateAsync's MessageId-keyed dedupe check would catch none of them. Pattern-match
                // (`is`), not a hard cast: ISagaContext<TState> is public and an external implementation
                // will not implement this internal interface.
                if (context is ISagaContextDeferredPublisher deferred)
                    deferred.ClearDeferredPublishes();

                VSagaDiagnostics.StepRetries.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, context.Saga.SagaType));
                await Task.Delay(step.RetryPolicy.DelayForAttempt(attempt), cancellationToken);
            }
        }
    }
}
