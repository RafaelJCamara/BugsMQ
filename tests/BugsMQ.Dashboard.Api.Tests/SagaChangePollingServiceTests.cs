using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Dashboard.Api.Hubs;
using BugsMQ.Persistence.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BugsMQ.Dashboard.Api.Tests;

/// <summary>
/// The poller is what actually delivers live updates in the deployed topology: sagas run in a different
/// process (the OrderProcessing sample), so <see cref="SignalRSagaChangeNotifier"/> never fires in the
/// dashboard and this diff-and-push loop is the only path. Its watermark logic had no coverage at all.
///
/// <para>
/// Exercises <c>PollOnceAsync</c> directly rather than driving the <see cref="PeriodicTimer"/>, so the
/// assertions are about the diff and the watermark rather than about winning a race with a background
/// task's continuation.
/// </para>
/// </summary>
public sealed class SagaChangePollingServiceTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly RecordingHubContext _hub = new();
    private readonly SagaChangePollingService _service;

    public SagaChangePollingServiceTests()
    {
        var services = new ServiceCollection();
        services.AddBugsMqInMemoryPersistence();
        _provider = services.BuildServiceProvider();

        _service = new SagaChangePollingService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _hub,
            NullLogger<SagaChangePollingService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _provider.Dispose();
    }

    private async Task<SagaSummary> SeedAsync(string sagaType, DateTimeOffset updatedAtUtc, Guid? correlationId = null)
    {
        var id = correlationId ?? Guid.NewGuid();
        var state = new DashboardTestState
        {
            CorrelationId = id,
            SagaType = sagaType,
            Kind = SagaKind.Orchestrated,
            CurrentState = "Running",
            Status = SagaStatus.Running,
            CreatedAtUtc = updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

        await _provider.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>().InsertAsync(state);
        return new SagaSummary(id, sagaType, state.Kind, state.CurrentState, state.Status, updatedAtUtc, updatedAtUtc, 0);
    }

    private List<string> GroupsPushedTo() => _hub.Recorder.SagaUpdates.Select(c => c.Group).ToList();

    [Fact]
    public async Task PushesEachChangedSagaToBothTheListAndItsInstanceGroup()
    {
        var since = DateTimeOffset.UtcNow;
        var saga = await SeedAsync("OrderSaga", since.AddSeconds(1));

        await _service.PollOnceAsync(since, CancellationToken.None);

        var groups = GroupsPushedTo();
        Assert.Contains(SagaHub.ListGroup, groups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.GroupForSaga("OrderSaga", saga.CorrelationId), groups, StringComparer.Ordinal);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public async Task IgnoresSagasNotUpdatedSinceTheWatermark()
    {
        var since = DateTimeOffset.UtcNow;
        await SeedAsync("OrderSaga", since.AddSeconds(-5));

        var next = await _service.PollOnceAsync(since, CancellationToken.None);

        Assert.Empty(_hub.Recorder.SagaUpdates);
        Assert.Equal(since, next);
    }

    /// <summary>
    /// The comparison is strictly greater-than, so a saga stamped exactly at the watermark is treated as
    /// already delivered. Pinned deliberately: this is the boundary that decides between re-pushing the
    /// previous tick's last saga forever and dropping one that landed on the same tick.
    /// </summary>
    [Fact]
    public async Task ASagaStampedExactlyAtTheWatermarkIsTreatedAsAlreadySeen()
    {
        var since = DateTimeOffset.UtcNow;
        await SeedAsync("OrderSaga", since);

        var next = await _service.PollOnceAsync(since, CancellationToken.None);

        Assert.Empty(_hub.Recorder.SagaUpdates);
        Assert.Equal(since, next);
    }

    [Fact]
    public async Task AdvancesTheWatermarkToTheNewestChangeSoTheNextTickIsQuiet()
    {
        var since = DateTimeOffset.UtcNow;
        await SeedAsync("OrderSaga", since.AddSeconds(1));
        var newest = since.AddSeconds(3);
        await SeedAsync("OtherSaga", newest);

        var next = await _service.PollOnceAsync(since, CancellationToken.None);
        Assert.Equal(newest, next);

        var pushesAfterFirstTick = _hub.Recorder.SagaUpdates.Count;

        // Nothing changed in between, so a second tick from the returned watermark must push nothing.
        var afterSecond = await _service.PollOnceAsync(next, CancellationToken.None);

        Assert.Equal(pushesAfterFirstTick, _hub.Recorder.SagaUpdates.Count);
        Assert.Equal(newest, afterSecond);
    }

    [Fact]
    public async Task PushesChangesOldestFirst()
    {
        var since = DateTimeOffset.UtcNow;
        var middle = await SeedAsync("B", since.AddSeconds(2));
        var oldest = await SeedAsync("A", since.AddSeconds(1));
        var newest = await SeedAsync("C", since.AddSeconds(3));

        await _service.PollOnceAsync(since, CancellationToken.None);

        // Two pushes per saga (list + instance); the per-saga order is what matters here.
        var order = _hub.Recorder.SagaUpdates
            .Select(c => c.Summary.CorrelationId)
            .Distinct()
            .ToList();

        Assert.Equal([oldest.CorrelationId, middle.CorrelationId, newest.CorrelationId], order);
    }

    /// <summary>
    /// The cross-process counterpart of the notifier test: when an orchestrated and a choreographed saga
    /// share one correlation id — exactly what the OrderProcessing sample now produces — the poller must
    /// address two distinct instance groups, not push both under one.
    /// </summary>
    [Fact]
    public async Task TwoSagaTypesSharingACorrelationIdArePushedToSeparateInstanceGroups()
    {
        var since = DateTimeOffset.UtcNow;
        var correlationId = Guid.NewGuid();
        await SeedAsync("OrderSaga", since.AddSeconds(1), correlationId);
        await SeedAsync("PostShipmentChoreography", since.AddSeconds(2), correlationId);

        await _service.PollOnceAsync(since, CancellationToken.None);

        var instanceGroups = GroupsPushedTo()
            .Where(g => !string.Equals(g, SagaHub.ListGroup, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, instanceGroups.Count);
        Assert.Contains(SagaHub.GroupForSaga("OrderSaga", correlationId), instanceGroups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.GroupForSaga("PostShipmentChoreography", correlationId), instanceGroups, StringComparer.Ordinal);
    }
}
