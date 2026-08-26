using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Sagas;

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
                VSagaDiagnostics.StepRetries.Add(1, new KeyValuePair<string, object?>(VSagaDiagnostics.TagSagaType, context.Saga.SagaType));
                await Task.Delay(step.RetryPolicy.DelayForAttempt(attempt), cancellationToken);
            }
        }
    }
}
