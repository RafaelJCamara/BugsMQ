using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Abstractions.Transport;
using BugsMQ.Persistence.InMemory;
using BugsMQ.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BugsMQ.Core.Tests;

/// <summary>
/// A saga can start another saga as a step, and the child records which instance started it.
///
/// <para>
/// <b>Every test here drives the real publish → receive → create-instance path.</b> Not a stylistic
/// preference: an earlier pass in this repo threaded SourceService/CausationId onto envelope headers,
/// and its tests hand-built <c>SagaLogEntry</c> objects with the field already populated — so they
/// passed for months while the orchestrator never read the header at all. A test that constructs the
/// parent link itself proves only that a record can hold two values. So nothing below ever assigns
/// <c>ParentSagaType</c>/<c>ParentCorrelationId</c>, or stamps a header by hand: the only way those
/// fields get set is <c>ctx.StartChildAsync</c> publishing, the transport delivering, and
/// <c>SagaOrchestrator</c> reading the headers back off the wire.
/// </para>
/// </summary>
public sealed class SubSagaCompositionTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly InMemoryMessageTransport _transport;
    private readonly ISagaSummaryReader _reader;

    public SubSagaCompositionTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddBugsMqInMemoryPersistence();
        services.AddBugsMqInMemoryTransport();

        // All three levels of the chain in one engine, exactly as the OrderProcessing sample registers
        // its parent and child side by side. No registration links them — the parent publishes and
        // whichever saga's CanInitiate matches becomes the child.
        services.AddBugsMqEngine(o => o
            .AddSaga<TestFulfilmentSaga, TestFulfilmentState>()
            .AddSaga<TestParcelSaga, TestParcelState>()
            .AddSaga<TestArchiveSaga, TestArchiveState>());

        _provider = services.BuildServiceProvider();
        _transport = (InMemoryMessageTransport)_provider.GetRequiredService<IMessageTransport>();
        _reader = _provider.GetRequiredService<ISagaSummaryReader>();

        foreach (var hosted in _provider.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hosted in _provider.GetServices<IHostedService>())
            await hosted.StopAsync(CancellationToken.None);

        await _provider.DisposeAsync();
    }

    /// <summary>Starts a root fulfilment saga and returns its correlation id; the chain below it runs synchronously through the in-memory transport.</summary>
    private async Task<Guid> StartFulfilmentAsync(string orderId)
    {
        var correlationId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginFulfilment(orderId), MessageEnvelope.New(correlationId));
        return correlationId;
    }

    [Fact]
    public async Task StartChildAsync_CreatesASeparateInstanceStampedWithTheParentPointer()
    {
        var parentId = await StartFulfilmentAsync("ORD-1");

        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), parentId));

        Assert.Equal(nameof(TestParcelSaga), child.SagaType);
        Assert.Equal(nameof(TestFulfilmentSaga), child.ParentSagaType);
        Assert.Equal(parentId, child.ParentCorrelationId);

        // The half that makes this a sub-saga rather than a second observer of the same transaction:
        // the child is a genuinely separate instance under an id the engine minted for it.
        Assert.NotEqual(parentId, child.CorrelationId);
    }

    [Fact]
    public async Task TheParentPointerIsOnThePersistedSnapshot_NotOnlyTheSummaryProjection()
    {
        var parentId = await StartFulfilmentAsync("ORD-2");
        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), parentId));

        // SagaSummary is a projection; this reads the saga's own persisted TState back, which is what a
        // child would actually consult to learn who started it.
        var snapshot = await _provider.GetRequiredService<ISagaSnapshotStore<TestParcelState>>()
            .FindAsync(nameof(TestParcelSaga), child.CorrelationId);

        Assert.NotNull(snapshot);
        Assert.Equal(nameof(TestFulfilmentSaga), snapshot.ParentSagaType);
        Assert.Equal(parentId, snapshot.ParentCorrelationId);
    }

    [Fact]
    public async Task ASagaNobodyStarted_HasNoParent()
    {
        var parentId = await StartFulfilmentAsync("ORD-3");

        var root = await _provider.GetRequiredService<ISagaSnapshotStore<TestFulfilmentState>>()
            .FindAsync(nameof(TestFulfilmentSaga), parentId);

        Assert.NotNull(root);
        Assert.Null(root.ParentSagaType);
        Assert.Null(root.ParentCorrelationId);
        Assert.Empty(await _reader.FindChildrenAsync(nameof(TestParcelSaga), parentId));
    }

    [Fact]
    public async Task FindChildrenAsync_ReturnsDirectChildrenOnly_NotTheWholeTree()
    {
        var parentId = await StartFulfilmentAsync("ORD-4");

        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), parentId));
        var grandchild = Assert.Single(await _reader.FindChildrenAsync(nameof(TestParcelSaga), child.CorrelationId));

        // Linkage survives a second hop rather than only happening to work at depth one.
        Assert.Equal(nameof(TestArchiveSaga), grandchild.SagaType);
        Assert.Equal(nameof(TestParcelSaga), grandchild.ParentSagaType);
        Assert.Equal(child.CorrelationId, grandchild.ParentCorrelationId);

        // And the root's children stop at one level: walking a tree is the caller's job, so a
        // grandchild must not silently appear under its grandparent.
        var rootChildren = await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), parentId);
        Assert.DoesNotContain(rootChildren, s => s.CorrelationId == grandchild.CorrelationId);
    }

    [Fact]
    public async Task AChildIsNotFoundByTheParentsCorrelationId()
    {
        var parentId = await StartFulfilmentAsync("ORD-5");

        // /api/correlations/{id} answers "which saga types track this business transaction" and a child
        // legitimately does not: it has its own id. The two relations are genuinely different questions,
        // which is why the dashboard renders them as separate strips.
        var sharingTheId = await _reader.FindByCorrelationIdAsync(parentId);

        var onlyOne = Assert.Single(sharingTheId);
        Assert.Equal(nameof(TestFulfilmentSaga), onlyOne.SagaType);
    }

    [Fact]
    public async Task AChildMessageNobodyInitiatesOn_StartsNothingAndTellsNobody()
    {
        // Failure mode worth pinning rather than discovering: StartChildAsync is a publish, so a message
        // type no saga initiates on produces no child and no error. The parent transitions and parks
        // exactly as it would have on success, and only its timeout eventually notices.
        var parentId = Guid.NewGuid();
        await _transport.PublishAsync(new BeginOrphanFulfilment("ORD-6"), MessageEnvelope.New(parentId));

        var parent = await _provider.GetRequiredService<ISagaSnapshotStore<TestFulfilmentState>>()
            .FindAsync(nameof(TestFulfilmentSaga), parentId);

        Assert.NotNull(parent);
        Assert.Equal(nameof(TestFulfilmentSaga.AwaitingChild), parent.CurrentState);
        Assert.Equal(SagaStatus.Running, parent.Status);
        Assert.Empty(await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), parentId));
    }

    [Fact]
    public async Task TheChildKeepsItsOwnTimelineUnderItsOwnCorrelationId()
    {
        var parentId = await StartFulfilmentAsync("ORD-7");
        var child = Assert.Single(await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), parentId));

        var log = _provider.GetRequiredService<ISagaEventLogStore>();
        var childTimeline = await log.GetTimelineAsync(nameof(TestParcelSaga), child.CorrelationId);

        Assert.Contains(childTimeline, e => e.EntryType == SagaEntryType.SagaStarted);
        Assert.All(childTimeline, e => Assert.Equal(child.CorrelationId, e.CorrelationId));
    }

    [Fact]
    public async Task StartingAChildIsRecordedOnTheParentsTimelineAsAnOrdinaryPublish()
    {
        // Documents a real Slice-1 gap rather than papering over it: the parent's timeline shows the
        // child start as a plain MessagePublished, indistinguishable from any other outbound message.
        // Making it distinguishable means a new SagaEntryType, which persists as a plain integer and is
        // append-only — deliberately deferred to the completion-notification slice.
        var parentId = await StartFulfilmentAsync("ORD-8");

        var log = _provider.GetRequiredService<ISagaEventLogStore>();
        var parentTimeline = await log.GetTimelineAsync(nameof(TestFulfilmentSaga), parentId);

        var published = Assert.Single(parentTimeline, e => e.EntryType == SagaEntryType.MessagePublished);
        Assert.Equal(nameof(DeliverParcel), published.MessageType);
        Assert.DoesNotContain(parentTimeline, e => e.EntryType == SagaEntryType.UnexpectedEvent);
    }

    [Fact]
    public async Task TwoParentsStartingChildrenDoNotSeeEachOthers()
    {
        var firstParent = await StartFulfilmentAsync("ORD-9");
        var secondParent = await StartFulfilmentAsync("ORD-10");

        var firstChild = Assert.Single(await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), firstParent));
        var secondChild = Assert.Single(await _reader.FindChildrenAsync(nameof(TestFulfilmentSaga), secondParent));

        Assert.NotEqual(firstChild.CorrelationId, secondChild.CorrelationId);
        Assert.Equal(firstParent, firstChild.ParentCorrelationId);
        Assert.Equal(secondParent, secondChild.ParentCorrelationId);
    }
}
