using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VSaga.Core.Runtime;

/// <summary>
/// Crash-recovery backstop for <c>ctx.PublishAfterCommitAsync</c> (production-readiness.md §4): polls
/// <see cref="ISagaOutboxStore"/> for rows still Pending past <see cref="SagaOutboxOptions.DispatchGracePeriod"/>
/// and republishes each one raw, by type name, through the same <see cref="IMessageTransport"/> every
/// other publish goes through. Not the dispatch path — <c>SagaOrchestrator</c>'s own inline drain
/// already sent everything that reached a committed persist; this only ever fires for a row a crash
/// caught between that persist and its drain.
/// </summary>
internal sealed class SagaOutboxDispatcherHostedService(
    IServiceScopeFactory scopeFactory,
    IMessageTransport transport,
    TimeProvider timeProvider,
    SagaOutboxOptions options,
    ILogger<SagaOutboxDispatcherHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval, timeProvider);

        do
        {
            try
            {
                var pending = await ClaimPendingAsync(stoppingToken);

                foreach (var message in pending)
                {
                    try
                    {
                        await RedispatchAsync(message, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // Already claimed (marked Dispatched) below, per ClaimPendingAsync's own
                        // claim-marks-Dispatched contract — matching SagaTimeoutDispatcherHostedService's
                        // identical claim-then-fire trade-off for ISagaTimeoutStore.ClaimDueAsync, this is
                        // an at-most-once redelivery attempt, not a retried one.
                        logger.LogError(ex,
                            "Failed to redispatch outbox message {Id} ({MessageType}) for saga {SagaType} correlation {CorrelationId}",
                            message.Id, message.MessageTypeName, message.SagaType, message.CorrelationId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error while polling for pending outbox messages");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private Task RedispatchAsync(SagaOutboxMessage message, CancellationToken cancellationToken)
    {
        var envelope = new MessageEnvelope(message.CorrelationId, message.MessageId, message.Headers);

        return message.Destination is null
            ? transport.PublishRawAsync(message.MessageTypeName, message.Body, envelope, cancellationToken)
            : transport.SendRawAsync(message.Destination, message.MessageTypeName, message.Body, envelope, cancellationToken);
    }

    /// <summary>
    /// Opens a fresh DI scope per poll so <see cref="ISagaOutboxStore"/> — Scoped under EF Core, one
    /// <c>DbContext</c> per unit of work — is never captured for this singleton
    /// <see cref="BackgroundService"/>'s process lifetime. Matches
    /// <see cref="SagaTimeoutDispatcherHostedService"/>'s own per-unit-of-work scoping (production-
    /// readiness.md §8 item 4, the captive-dependency bug this deliberately avoids repeating).
    /// </summary>
    private async Task<IReadOnlyList<SagaOutboxMessage>> ClaimPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var outboxStore = scope.ServiceProvider.GetRequiredService<ISagaOutboxStore>();
        var olderThan = timeProvider.GetUtcNow() - options.DispatchGracePeriod;
        return await outboxStore.ClaimPendingAsync(olderThan, options.BatchSize, cancellationToken);
    }
}
