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

/// <summary>
/// Verifies SagaOrchestrator's bounded redelivery for infrastructure-level failures (a persistence
/// store exception, not a saga step's own thrown exception — HandleStepFailureAsync already covers
/// that case elsewhere). There's no way to force this failure mode through the public API surface, so
/// these tests decorate the event log store with one that fails a controlled number of times.
/// </summary>
public sealed class SagaOrchestratorInfrastructureFailureTests
{
    /// <summary>Throws on the first <paramref name="failuresBeforeSuccess"/> calls, then delegates to the real store.</summary>
    private sealed class FlakyEventLogStore(ISagaEventLogStore inner, int failuresBeforeSuccess) : ISagaEventLogStore
    {
        private int _remainingFailures = failuresBeforeSuccess;

        public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
        {
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new InvalidOperationException("simulated transient infrastructure failure");
            }

            return inner.AppendAsync(entry, cancellationToken);
        }

        public Task<bool> IsDuplicateAsync(string sagaType, Guid correlationId, string messageId, CancellationToken cancellationToken = default) =>
            inner.IsDuplicateAsync(sagaType, correlationId, messageId, cancellationToken);

        public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.GetTimelineAsync(sagaType, correlationId, cancellationToken);
    }

    private static async Task<ServiceProvider> BuildProviderAsync(int failuresBeforeSuccess, int maxDeliveryAttempts)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBugsMqInMemoryPersistence();
        services.AddBugsMqInMemoryTransport();
        services.AddSingleton(new SagaOrchestratorOptions { MaxDeliveryAttempts = maxDeliveryAttempts });
        services.AddBugsMqEngine(o => o.AddSaga<TestOrderSaga, TestOrderSagaState>());

        // Registered after AddBugsMqInMemoryPersistence so it wins service resolution, wrapping the
        // same underlying InMemorySagaStore the rest of the engine (summary reader, timeout store) still
        // reads/writes directly — only the event log's AppendAsync is made flaky.
        services.AddSingleton<ISagaEventLogStore>(sp =>
            new FlakyEventLogStore(sp.GetRequiredService<InMemorySagaStore>(), failuresBeforeSuccess));

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    [Fact]
    public async Task InfrastructureFailure_ThatStopsRecurring_RedeliversAndSucceeds()
    {
        await using var provider = await BuildProviderAsync(failuresBeforeSuccess: 1, maxDeliveryAttempts: 5);
        var transport = (InMemoryMessageTransport)provider.GetRequiredService<IMessageTransport>();
        var correlationId = Guid.NewGuid();

        // Single await: the redelivery happens synchronously/recursively through the in-memory
        // transport's inline dispatch, so by the time this returns the whole fail-then-recover
        // sequence has already completed.
        await transport.PublishAsync(new OrderSubmitted("ORD-INFRA-1", 15m), MessageEnvelope.New(correlationId));

        var sagaType = provider.GetRequiredService<TestOrderSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        var state = await snapshotStore.FindAsync(sagaType, correlationId);

        Assert.NotNull(state);
        Assert.Equal(SagaStatus.Running, state.Status);
        Assert.Equal("AwaitingInventory", state.CurrentState);
        Assert.Contains(transport.GetPublished(), p => p.Message is ReserveInventory);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InfrastructureFailure_ThatNeverRecovers_IsDeadLetteredAfterMaxAttempts()
    {
        // Fails one more time than the redelivery cap allows, so recovery never happens within budget.
        await using var provider = await BuildProviderAsync(failuresBeforeSuccess: 3, maxDeliveryAttempts: 2);
        var transport = (InMemoryMessageTransport)provider.GetRequiredService<IMessageTransport>();
        var correlationId = Guid.NewGuid();

        await transport.PublishAsync(new OrderSubmitted("ORD-INFRA-2", 9m), MessageEnvelope.New(correlationId));

        // The saga never got past its first (always-failing) log append on any of the 3 attempts, so no
        // snapshot was ever created — the only durable trace is the DeliveryExhausted entry itself.
        var sagaType = provider.GetRequiredService<TestOrderSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<TestOrderSagaState>>();
        Assert.Null(await snapshotStore.FindAsync(sagaType, correlationId));

        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();
        var timeline = await eventLog.GetTimelineAsync(sagaType, correlationId);
        var exhausted = Assert.Single(timeline, e => e.EntryType == SagaEntryType.DeliveryExhausted);
        Assert.Equal(nameof(OrderSubmitted), exhausted.MessageType);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }
}
