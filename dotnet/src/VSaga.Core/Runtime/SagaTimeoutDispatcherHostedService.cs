using VSaga.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VSaga.Core.Runtime;

/// <summary>Polls <see cref="ISagaTimeoutStore"/> for due timeouts and dispatches each to the owning saga's runtime.</summary>
internal sealed class SagaTimeoutDispatcherHostedService(
    IEnumerable<ISagaRuntime> runtimes,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SagaTimeoutDispatcherHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runtimesBySagaType = runtimes.ToDictionary(r => r.SagaType, StringComparer.Ordinal);
        if (runtimesBySagaType.Count == 0)
            return;

        using var timer = new PeriodicTimer(PollInterval, timeProvider);

        do
        {
            try
            {
                var due = await ClaimDueTimeoutsAsync(stoppingToken);

                foreach (var timeout in due)
                {
                    if (!runtimesBySagaType.TryGetValue(timeout.SagaType, out var runtime))
                    {
                        logger.LogWarning("No registered saga runtime for timeout of unknown saga type {SagaType}", timeout.SagaType);
                        continue;
                    }

                    try
                    {
                        await runtime.HandleTimeoutAsync(timeout, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to handle timeout {TimeoutId} for saga {CorrelationId}", timeout.Id, timeout.CorrelationId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error while polling for due saga timeouts");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Opens a fresh DI scope per poll so <see cref="ISagaTimeoutStore"/> — Scoped under EF Core,
    /// one <c>DbContext</c> per unit of work — is never captured for this singleton
    /// <see cref="BackgroundService"/>'s process lifetime. Matches <see cref="SagaRuntime{TState}"/>'s
    /// own per-unit-of-work scoping.
    /// </summary>
    private async Task<IReadOnlyList<SagaTimeout>> ClaimDueTimeoutsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var timeoutStore = scope.ServiceProvider.GetRequiredService<ISagaTimeoutStore>();
        return await timeoutStore.ClaimDueAsync(timeProvider.GetUtcNow(), BatchSize, cancellationToken);
    }
}
