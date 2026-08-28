using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;
using VSaga.Core.Dsl;
using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// production-readiness.md §5.4 (item 15): empirically settles the section's own open question --
/// does a <see cref="SagaConcurrencyException"/> raised from the final <c>PersistAsync</c> call inside
/// <c>RunStepAsync</c> actually reach <c>HandleInfrastructureFailureAsync</c> and trigger ordinary
/// redelivery, or does it get swallowed (caught-and-logged, or mistaken for an ordinary business-level
/// step failure) on the way out? Traced in <c>SagaOrchestrator.cs</c>: <c>HandleStepSuccessAsync</c>'s
/// <c>PersistAsync</c> call (the one after a successful <c>definition.HandleAsync</c>) sits outside
/// <c>RunStepAsync</c>'s own try/catch, which only wraps <c>definition.HandleAsync</c> itself -- so the
/// exception propagates untouched through <c>RunStepAsync</c> and <c>HandleCoreAsync</c> straight into
/// <c>HandleAsync</c>'s outer catch. Answer: it does reach redelivery, confirmed below rather than just
/// by inspection.
/// <para>
/// This is also the scenario §5.4 describes for <c>HttpInboundDispatcher</c>: two messages resolving to
/// the same saga instance via a shared business key (§5.2/§5.3) but carrying two <em>different</em>
/// transport correlation ids, so the dispatcher's gate -- keyed on <c>received.CorrelationId</c> -- does
/// not serialize them against each other. The race is reproduced here the same controlled-fake way
/// <c>SagaOrchestratorBusinessKeyRaceTests</c>/<c>SagaOrchestratorTimeoutRaceTests</c> reproduce theirs,
/// since there is no reliable way to force the interleaving through real timing.
/// </para>
/// </summary>
public sealed class SagaOrchestratorConcurrencyRedeliveryTests
{
    public sealed class ConcurrencyRedeliveryRaceSagaState : SagaState
    {
        public string? OrderId { get; set; }
    }

    public sealed record OrderOpened(string OrderId);
    public sealed record ConfirmationReceived(string OrderId);
    public sealed record OrderConfirmed(string OrderId);

    /// <summary>Same fixture-per-file rationale as <c>SagaOrchestratorBusinessKeyRaceTests.BusinessKeyRaceSaga</c>: a minimal saga that declares CorrelateOn, kept separate from the 100+ tests sharing TestOrderSaga.</summary>
    public sealed class ConcurrencyRedeliveryRaceSaga : OrchestratedSagaDefinition<ConcurrencyRedeliveryRaceSagaState>
    {
        public State<ConcurrencyRedeliveryRaceSagaState> Open { get; }
        public State<ConcurrencyRedeliveryRaceSagaState> Confirmed { get; }

        public ConcurrencyRedeliveryRaceSaga()
        {
            Open = InitialState(nameof(Open));
            Confirmed = State(nameof(Confirmed));

            CorrelateOn(s => s.OrderId);

            During(Open)
                .When<OrderOpened>()
                    .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .When<ConfirmationReceived>()
                    .CorrelateBy(m => m.OrderId, s => s.OrderId)
                    .Publish((ctx, m) => new OrderConfirmed(m.OrderId))
                    .TransitionTo(Confirmed);
        }
    }

    /// <summary>
    /// Wraps the real snapshot store. When <see cref="OnNextFindByBusinessKey"/> is set, the very next
    /// <see cref="FindByBusinessKeyAsync"/> call runs it synchronously -- after capturing the pre-race
    /// snapshot but before returning it -- then clears itself so nothing else (notably the triggered
    /// message's own lookup) is affected. Same controlled-fake shape as the sibling race tests'
    /// RaceInjectingSnapshotStore, just hooked on the business-key lookup instead of FindAsync/InsertAsync.
    /// </summary>
    private sealed class RaceInjectingSnapshotStore<TState>(ISagaSnapshotStore<TState> inner) : ISagaSnapshotStore<TState>
        where TState : SagaState
    {
        public Func<Task>? OnNextFindByBusinessKey { get; set; }

        public Task<TState?> FindAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(sagaType, correlationId, cancellationToken);

        public Task InsertAsync(TState state, CancellationToken cancellationToken = default) =>
            inner.InsertAsync(state, cancellationToken);

        public Task UpdateAsync(TState state, int expectedVersion, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(state, expectedVersion, cancellationToken);

        public async Task<TState?> FindByBusinessKeyAsync(string sagaType, string businessKey, CancellationToken cancellationToken = default)
        {
            var state = await inner.FindByBusinessKeyAsync(sagaType, businessKey, cancellationToken);

            var trigger = OnNextFindByBusinessKey;
            if (trigger is not null)
            {
                OnNextFindByBusinessKey = null;
                await trigger();
            }

            return state;
        }
    }

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        services.AddVSagaEngine(o => o.AddSaga<ConcurrencyRedeliveryRaceSaga, ConcurrencyRedeliveryRaceSagaState>());

        // Registered after AddVSagaInMemoryPersistence so it wins service resolution, wrapping the same
        // underlying InMemorySagaStore the rest of the engine still reads/writes directly -- see
        // SagaOrchestratorBusinessKeyRaceTests for the same override-order trick.
        services.AddSingleton<ISagaSnapshotStore<ConcurrencyRedeliveryRaceSagaState>>(sp =>
            new RaceInjectingSnapshotStore<ConcurrencyRedeliveryRaceSagaState>(new InMemorySagaSnapshotStore<ConcurrencyRedeliveryRaceSagaState>(sp.GetRequiredService<InMemorySagaStore>())));

        var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        return provider;
    }

    [Fact]
    public async Task ConcurrentUpdatesToSameInstanceViaDifferentCorrelationIds_ExceptionReachesRedeliveryButTheLoserIsDroppedNotRetried()
    {
        const string businessKey = "ORD-CONCURRENCY-1";
        var openCorrelationId = Guid.NewGuid();
        var correlationIdB = Guid.NewGuid();
        var correlationIdC = Guid.NewGuid();
        var messageIdB = Guid.NewGuid().ToString("N");

        await using var provider = await BuildProviderAsync();
        var transport = (InMemoryMessageTransport)provider.GetRequiredService<IMessageTransport>();
        var sagaType = provider.GetRequiredService<ConcurrencyRedeliveryRaceSaga>().SagaType;
        var snapshotStore = provider.GetRequiredService<ISagaSnapshotStore<ConcurrencyRedeliveryRaceSagaState>>();
        var eventLog = provider.GetRequiredService<ISagaEventLogStore>();

        // Create the instance for real: OrderOpened reserves the business key and inserts the row.
        await transport.PublishAsync(new OrderOpened(businessKey), MessageEnvelope.New(openCorrelationId));

        var opened = await snapshotStore.FindByBusinessKeyAsync(sagaType, businessKey);
        Assert.NotNull(opened);
        var initialVersion = opened.Version;

        // Arm the race for message B's upcoming business-key lookup: right after B reads the pre-race
        // snapshot (still at initialVersion) but before ResolveInstanceAsync returns it, synchronously
        // deliver and fully process message C first -- same business key, a DIFFERENT fresh transport
        // correlation id, neither aware of the other, and neither equal to the instance's own
        // correlation id (openCorrelationId). This is precisely the §5.3 "reply observed under its own
        // fresh id" scenario that reaches the existing instance via the business-key fallback -- and
        // precisely what HttpInboundDispatcher's gate (keyed on received.CorrelationId, §5.4) cannot
        // serialize, since B and C never share a key in its ConcurrentDictionary. C's own lookup runs
        // after the hook has cleared itself, so it sees the same pre-race snapshot as B captured,
        // transitions to Confirmed, and persists first -- landing at initialVersion + 1.
        var racingStore = (RaceInjectingSnapshotStore<ConcurrencyRedeliveryRaceSagaState>)snapshotStore;
        racingStore.OnNextFindByBusinessKey = () =>
            transport.PublishAsync(new ConfirmationReceived(businessKey), MessageEnvelope.New(correlationIdC));

        // B now loses: its own step runs (including its own Publish -- see AssertDurableBackstopHeld's
        // comment on why that still goes out) against the stale initialVersion snapshot it already
        // captured, and PersistAsync's UpdateAsync(expectedVersion: initialVersion) throws
        // SagaConcurrencyException the moment it sees C's write already landed. Awaiting this call to
        // completion is what proves the exception doesn't propagate out uncaught -- HandleAsync's own
        // try/catch must have handled it.
        await transport.PublishAsync(new ConfirmationReceived(businessKey), new MessageEnvelope(correlationIdB, messageIdB));

        var winner = await snapshotStore.FindByBusinessKeyAsync(sagaType, businessKey);
        Assert.NotNull(winner);
        var timeline = await eventLog.GetTimelineAsync(sagaType, winner.CorrelationId);

        AssertDurableBackstopHeld(transport, winner, initialVersion);
        AssertConcurrencyExceptionReachedRedeliveryUnswallowed(transport, timeline, winner, correlationIdB);
        AssertRedeliveryWasADeadEndForTheLoser(timeline, messageIdB);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// §5.4's "not the gate, the Version check" claim, read precisely: the Version check decides whose
    /// STATE transition survives (exactly one instance, exactly one persisted transition, no
    /// corruption) -- but it cannot un-send a publish that already went out. <c>.Publish(...)</c> is one
    /// of the step's ordinary actions, run synchronously inside <c>definition.HandleAsync</c> (see
    /// EventBuilder.Publish), which is *before* PersistAsync ever runs, let alone fails -- so both B's
    /// and C's own OrderConfirmed went out for real. This was the first, surprising thing this test
    /// caught empirically: a hand-derived expectation of "only the winner's side effect is sent" is
    /// wrong for an ordinary (non-deferred) <c>.Publish(...)</c>, which is exactly why this item calls
    /// for an executed test over reasoning from the code alone.
    /// </summary>
    private static void AssertDurableBackstopHeld(InMemoryMessageTransport transport, ConcurrencyRedeliveryRaceSagaState winner, int initialVersion)
    {
        Assert.Equal(nameof(ConcurrencyRedeliveryRaceSaga.Confirmed), winner.CurrentState);
        Assert.Equal(initialVersion + 1, winner.Version); // only one transition was ever persisted
        Assert.Equal(2, transport.GetPublished().Count(p => p.Message is OrderConfirmed)); // both racers' side effects went out regardless
    }

    /// <summary>
    /// The open question this test settles empirically: yes, a SagaConcurrencyException from the final
    /// PersistAsync reaches HandleInfrastructureFailureAsync and triggers an ordinary redelivery publish
    /// (same MessageId, delivery-attempt header incremented to 1) -- it is not swallowed on the way out,
    /// and it was NOT instead caught by RunStepAsync's own catch and treated as an ordinary
    /// business-level step failure (which would mark the saga Failed and log StepFailed).
    /// </summary>
    private static void AssertConcurrencyExceptionReachedRedeliveryUnswallowed(InMemoryMessageTransport transport,
        IReadOnlyList<SagaLogEntry> timeline, ConcurrencyRedeliveryRaceSagaState winner, Guid correlationIdB)
    {
        var redelivery = Assert.Single(transport.GetPublished(), p =>
            string.Equals(p.MessageTypeName, nameof(ConfirmationReceived), StringComparison.Ordinal) &&
            p.Envelope.CorrelationId == correlationIdB &&
            p.Envelope.Headers is not null && p.Envelope.Headers.ContainsKey("x-vsaga-delivery-attempt"));
        Assert.Equal("1", redelivery.Envelope.Headers!["x-vsaga-delivery-attempt"]);

        Assert.DoesNotContain(timeline, e => e.EntryType == SagaEntryType.StepFailed);
        Assert.Equal(SagaStatus.Running, winner.Status);
    }

    /// <summary>
    /// The redelivery this triggers is a dead end for B, not a retry-to-success: it is acked and never
    /// dead-lettered, yet B's own state transition is gone for good, not merely delayed. This matters
    /// for reading the "durable guard" claim correctly -- it prevents corruption, it does not guarantee
    /// the loser's own message is eventually applied. HandleInfrastructureFailureAsync reuses B's
    /// original MessageId (by design, for exactly this dedupe), and RunStepAsync had already durably
    /// logged a MessageReceived entry for that exact MessageId (under the winner's own CorrelationId,
    /// via the business-key reassignment) before the failing persist -- so when the redelivered copy
    /// comes back around, HandleCoreAsync's IsDuplicateAsync check recognizes it as already-seen and
    /// silently skips it instead of reprocessing it: B's own MessageId appears exactly once in the
    /// timeline, not twice, proving the redelivery was never actually reprocessed.
    /// </summary>
    private static void AssertRedeliveryWasADeadEndForTheLoser(IReadOnlyList<SagaLogEntry> timeline, string messageIdB)
    {
        Assert.Single(timeline, e => e.EntryType == SagaEntryType.MessageReceived &&
            string.Equals(e.MessageId, messageIdB, StringComparison.Ordinal));
    }
}
