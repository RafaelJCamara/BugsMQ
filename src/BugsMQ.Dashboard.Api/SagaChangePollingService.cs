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
                // ISagaSummaryReader is Scoped (EF Core's DbContext needs to be) — this singleton
                // background service opens a fresh scope per tick rather than capturing one instance.
                await using var scope = scopeFactory.CreateAsyncScope();
                var reader = scope.ServiceProvider.GetRequiredService<ISagaSummaryReader>();

                var page = await reader.ListAsync(new SagaListFilter { PageSize = 100 }, stoppingToken);
                var changed = page.Items.Where(s => s.UpdatedAtUtc > since).OrderBy(s => s.UpdatedAtUtc).ToList();

                if (changed.Count == 0)
                    continue;

                since = changed[^1].UpdatedAtUtc;

                foreach (var summary in changed)
                {
                    await hub.Clients.Group(SagaHub.ListGroup).SagaUpdated(summary);
                    await hub.Clients.Group(SagaHub.GroupForSaga(summary.CorrelationId)).SagaUpdated(summary);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error polling for saga changes");
            }
        }
    }
}
