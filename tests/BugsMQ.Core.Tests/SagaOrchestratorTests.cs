using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Abstractions.Transport;
using BugsMQ.Core.Runtime;
using BugsMQ.Persistence.InMemory;
using BugsMQ.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BugsMQ.Core.Tests;

public sealed class SagaOrchestratorTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly TestOrderSaga _saga;

    public SagaOrchestratorTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBugsMqInMemoryPersistence();
        services.AddBugsMqInMemoryTransport();
        services.AddBugsMqEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());

        _provider = services.BuildServiceProvider();
        _transport = (InMemoryMessageTransport)_provider.GetRequiredService<IMessageTransport>();
        _saga = _provider.GetRequiredService<TestOrderSaga>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task HappyPath_RunsThroughToCompleted()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-1", 42m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentCharged(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(correlationId);

        Assert.NotNull(state);
        Assert.Equal(_saga.Completed.Name, state!.CurrentState);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Equal("ORD-1", state.OrderId);
        Assert.Equal(42m, state.Amount);

        var published = _transport.Published.Select(p => p.Message).ToList();
        Assert.Contains(published, m => m is ReserveInventory rq && rq.OrderId == "ORD-1");
        Assert.Contains(published, m => m is ChargePayment cp && cp.Amount == 42m);

        var summaryReader = _provider.GetRequiredService<ISagaSummaryReader>();
        var summary = await summaryReader.GetAsync(correlationId);
        Assert.NotNull(summary);
        Assert.Equal(SagaStatus.Completed, summary!.Status);
    }

    [Fact]
    public async Task FailurePath_CompensatesAndMarksFailed()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-2", 10m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new PaymentFailed(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(correlationId);

        Assert.NotNull(state);
        Assert.Equal(_saga.Failed.Name, state!.CurrentState);
        Assert.Equal(SagaStatus.Failed, state.Status);

        var published = _transport.Published.Select(p => p.Message).ToList();
        Assert.Contains(published, m => m is ReleaseInventory);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.SagaStarted);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.SagaCompleted); // logged for any terminal Finalize, including Failed
    }

    [Fact]
    public async Task NonInitiatingMessageForUnknownSaga_IsIgnoredWithoutCreatingState()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(correlationId);
        Assert.Null(state);

        var eventLog = _provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(correlationId);
        Assert.Contains(timeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
    }

    [Fact]
    public async Task ManualRetry_ReplaysFailedStepAndRecovers()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-3", 5m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new FlakyStep(), MessageEnvelope.New(correlationId));

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var afterFailure = await snapshotStore.FindAsync(correlationId);
        Assert.NotNull(afterFailure);
        Assert.Equal(SagaStatus.Failed, afterFailure!.Status);
        Assert.Equal(_saga.AwaitingInventory.Name, afterFailure.CurrentState); // failure keeps the saga parked in the state it failed in

        var retryDispatcher = _provider.GetRequiredService<ISagaRetryDispatcher>();
        await retryDispatcher.RetryAsync(_saga.SagaType, correlationId);

        var afterRetry = await snapshotStore.FindAsync(correlationId);
        Assert.NotNull(afterRetry);
        Assert.Equal(SagaStatus.Running, afterRetry!.Status);
        Assert.Equal(_saga.AwaitingPayment.Name, afterRetry.CurrentState);
        Assert.Equal(2, _saga.FlakyStepAttempts);
    }

    [Fact]
    public async Task Timeout_FiresAndTransitionsSaga()
    {
        var correlationId = Guid.NewGuid();

        await _transport.PublishAsync(new OrderSubmitted("ORD-4", 7m), MessageEnvelope.New(correlationId));
        await _transport.PublishAsync(new InventoryReserved(), MessageEnvelope.New(correlationId));

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var due = await timeoutStore.ClaimDueAsync(DateTimeOffset.UtcNow.AddHours(1), batchSize: 10);
        var timeout = Assert.Single(due, t => t.CorrelationId == correlationId);
        Assert.Equal(_saga.AwaitingPayment.Name, timeout.ForState);

        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<TestOrderSagaState>>();
        await orchestrator.HandleTimeoutAsync(timeout, CancellationToken.None);

        var snapshotStore = _provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(correlationId);
        Assert.NotNull(state);
        Assert.Equal(_saga.Failed.Name, state!.CurrentState);
        Assert.Equal(SagaStatus.TimedOut, state.Status);
    }
}
