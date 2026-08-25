using BugsMQ.Abstractions.Persistence;
using BugsMQ.Dashboard.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BugsMQ.Dashboard.Api;

/// <summary>
/// Saga processing typically happens in a different process than the dashboard (e.g. the sample's
/// OrderProcessing host) — <see cref="Hubs.SignalRSagaChangeNotifier"/> only fires for an orchestrator
/// running in *this* process, which won't happen unless the dashboard is also configured with its own
/// AddSaga&lt;&gt; registrations. This background poller is what actually delivers near-live updates
/// for the common case: it periodically diffs the most-recently-updated sagas against what it saw
/// last tick and pushes SignalR updates for whatever changed. A future BugsMQ.Chaos/scale-out story
/// could replace this with a message-bus-relayed push; polling every second is a reasonable v1 trade-off.
/// </summary>
internal sealed class SagaChangePollingService(
    IServiceScopeFactory scopeFactory,
    IHubContext<SagaHub, ISagaHubClient> hub,
    ILogger<SagaChangePollingService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var since = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                since = await PollOnceAsync(since, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately swallowed so one bad tick (a transient database blip, say) doesn't tear
                // down the loop and silently end live updates for every connected dashboard. `since` is
                // left untouched, so whatever changed during the failed tick is picked up by the next one.
                logger.LogError(ex, "Error polling for saga changes");
            }
        }
    }

    /// <summary>
    /// One poll tick: diffs the most-recently-updated sagas against <paramref name="since"/> and pushes
    /// a SignalR update for each change, returning the new watermark to poll from next time.
    /// <para>
    /// Split out of the timer loop so the diff/push logic is directly testable without driving a real
    /// <see cref="PeriodicTimer"/> — the alternative was a test that advances a clock and races the
    /// background task's continuation.
    /// </para>
    /// </summary>
    internal async Task<DateTimeOffset> PollOnceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        // ISagaSummaryReader is Scoped (EF Core's DbContext needs to be) — this singleton
        // background service opens a fresh scope per tick rather than capturing one instance.
        await using var scope = scopeFactory.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISagaSummaryReader>();

        var page = await reader.ListAsync(new SagaListFilter { PageSize = 100 }, cancellationToken);
        var changed = page.Items.Where(s => s.UpdatedAtUtc > since).OrderBy(s => s.UpdatedAtUtc).ToList();

        if (changed.Count == 0)
            return since;

        foreach (var summary in changed)
        {
            await hub.Clients.Group(SagaHub.ListGroup).SagaUpdated(summary);
            await hub.Clients.Group(SagaHub.GroupForSaga(summary.SagaType, summary.CorrelationId)).SagaUpdated(summary);
        }

        // Advanced only after the pushes succeed: if one throws, ExecuteAsync's catch leaves the old
        // watermark in place and the next tick retries the same window rather than skipping past it.
        return changed[^1].UpdatedAtUtc;
    }
}
