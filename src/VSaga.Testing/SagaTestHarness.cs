using VSaga.Abstractions.Notifications;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core;
using VSaga.Core.Dsl;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace VSaga.Testing;

/// <summary>
/// Given/when/then-style test wrapper around one saga definition, running the real
/// <see cref="SagaOrchestrator{TState}"/> against the in-memory persistence and transport
/// providers — so a test exercises the exact same engine code path as production, minus any
/// broker/database. Includes a <see cref="FakeTimeProvider"/> so timeout behavior is testable
/// without real waiting.
/// </summary>
public sealed class SagaTestHarness<TDefinition, TState> : IAsyncDisposable
    where TDefinition : class, ISagaDefinition<TState>
    where TState : SagaState, new()
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;

    public TDefinition Saga { get; }

    public FakeTimeProvider TimeProvider { get; }

    public Guid CorrelationId { get; private set; } = Guid.NewGuid();

    public IServiceProvider Services => _provider;

    public SagaTestHarness(Action<IServiceCollection>? configureServices = null)
    {
        TimeProvider = new FakeTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<TimeProvider>(TimeProvider);
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<TDefinition, TState>());
        configureServices?.Invoke(services);

        _provider = services.BuildServiceProvider();
        _transport = (InMemoryMessageTransport)_provider.GetRequiredService<IMessageTransport>();
        Saga = _provider.GetRequiredService<TDefinition>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Sets the correlation id subsequent When/Assert calls act on. Defaults to a fresh random id.</summary>
    public SagaTestHarness<TDefinition, TState> Given(Guid correlationId)
    {
        CorrelationId = correlationId;
        return this;
    }

    /// <summary>Publishes a message under the current correlation id and waits for it to be fully processed (the in-memory transport dispatches synchronously).</summary>
    public async Task<SagaTestHarness<TDefinition, TState>> WhenAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        await _transport.PublishAsync(message, MessageEnvelope.New(CorrelationId), cancellationToken);
        return this;
    }

    /// <summary>Advances the fake clock and processes any timeouts that are now due — the deterministic alternative to real waiting.</summary>
    public async Task<SagaTestHarness<TDefinition, TState>> AdvanceTimeByAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        TimeProvider.Advance(duration);

        var timeoutStore = _provider.GetRequiredService<ISagaTimeoutStore>();
        var orchestrator = _provider.GetRequiredService<SagaOrchestrator<TState>>();
        var due = await timeoutStore.ClaimDueAsync(TimeProvider.GetUtcNow(), batchSize: 1000, cancellationToken);

        foreach (var timeout in due.Where(t => string.Equals(t.SagaType, Saga.SagaType, StringComparison.Ordinal)))
            await orchestrator.HandleTimeoutAsync(timeout, cancellationToken);

        return this;
    }

    /// <summary>Triggers the same manual whole-saga retry the dashboard's Retry button would (only valid while the saga is Failed).</summary>
    public async Task<SagaTestHarness<TDefinition, TState>> RetryAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = _provider.GetRequiredService<ISagaRetryDispatcher>();
        await dispatcher.RetryAsync(Saga.SagaType, CorrelationId, cancellationToken);
        return this;
    }

    public Task<TState?> FindStateAsync(CancellationToken cancellationToken = default)
    {
        var store = _provider.GetRequiredService<ISagaSnapshotStore<TState>>();
        return store.FindAsync(Saga.SagaType, CorrelationId, cancellationToken);
    }

    public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(CancellationToken cancellationToken = default)
    {
        var log = _provider.GetRequiredService<ISagaEventLogStore>();
        return log.GetTimelineAsync(Saga.SagaType, CorrelationId, cancellationToken);
    }

    /// <summary>All messages published so far across every correlation id in this harness (publish and send).</summary>
    public IReadOnlyList<object> GetPublished() => _transport.GetPublished().Select(p => p.Message).OfType<object>().ToList();

    public async Task<TState> AssertStateAsync(State<TState> expected, CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken);

        if (!string.Equals(state.CurrentState, expected.Name, StringComparison.Ordinal))
            throw new SagaAssertionException($"Expected saga '{CorrelationId}' to be in state '{expected.Name}' but it was in '{state.CurrentState}'.");

        return state;
    }

    public async Task<TState> AssertStatusAsync(SagaStatus expected, CancellationToken cancellationToken = default)
    {
        var state = await RequireStateAsync(cancellationToken);

        if (state.Status != expected)
            throw new SagaAssertionException($"Expected saga '{CorrelationId}' to have status '{expected}' but it was '{state.Status}'.");

        return state;
    }

    public void AssertPublished<TMessage>(Func<TMessage, bool>? predicate = null)
    {
        if (!GetPublished().OfType<TMessage>().Any(m => predicate is null || predicate(m)))
            throw new SagaAssertionException($"Expected a published message of type '{typeof(TMessage).Name}' matching the predicate, but none was found.");
    }

    public void AssertNotPublished<TMessage>(Func<TMessage, bool>? predicate = null)
    {
        if (GetPublished().OfType<TMessage>().Any(m => predicate is null || predicate(m)))
            throw new SagaAssertionException($"Expected no published message of type '{typeof(TMessage).Name}' matching the predicate, but at least one was found.");
    }

    public async Task AssertNoSagaCreatedAsync(CancellationToken cancellationToken = default)
    {
        var state = await FindStateAsync(cancellationToken);
        if (state is not null)
            throw new SagaAssertionException($"Expected no saga instance for correlation id '{CorrelationId}', but one was found in state '{state.CurrentState}'.");
    }

    private async Task<TState> RequireStateAsync(CancellationToken cancellationToken) =>
        await FindStateAsync(cancellationToken)
        ?? throw new SagaAssertionException($"No saga instance was found for correlation id '{CorrelationId}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }
}
