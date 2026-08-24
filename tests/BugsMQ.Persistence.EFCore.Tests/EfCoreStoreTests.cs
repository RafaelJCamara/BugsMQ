using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BugsMQ.Persistence.EFCore.Tests;

public sealed class TestState : SagaState
{
    public string? OrderId { get; set; }
}

public sealed class EfCoreStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<BugsMqDbContext> _options;

    public EfCoreStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<BugsMqDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new BugsMqDbContext(_options);
        db.Database.EnsureCreated();
    }

    private BugsMqDbContext NewContext() => new(_options);

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task InsertAndFind_RoundTripsTypedState()
    {
        await using var db = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db);

        var correlationId = Guid.NewGuid();
        var state = new TestState
        {
            CorrelationId = correlationId,
            SagaType = "TestSaga",
            Kind = SagaKind.Orchestrated,
            CurrentState = "Started",
            Status = SagaStatus.Running,
            OrderId = "ORD-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.InsertAsync(state);

        await using var db2 = NewContext();
        var found = await new EfCoreSagaSnapshotStore<TestState>(db2).FindAsync(correlationId);

        Assert.NotNull(found);
        Assert.Equal("ORD-1", found!.OrderId);
        Assert.Equal("Started", found.CurrentState);
        Assert.Equal(0, found.Version);
    }

    [Fact]
    public async Task Update_WithCorrectVersion_SucceedsAndBumpsVersion()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Started",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        TestState? state;
        await using (var db2 = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db2);
            state = await store.FindAsync(correlationId);
            Assert.NotNull(state);

            state!.CurrentState = "Next";
            await store.UpdateAsync(state, expectedVersion: 0);

            Assert.Equal(1, state.Version);
        }

        await using var db3 = NewContext();
        var reloaded = await new EfCoreSagaSnapshotStore<TestState>(db3).FindAsync(correlationId);
        Assert.Equal("Next", reloaded!.CurrentState);
        Assert.Equal(1, reloaded.Version);
    }

    [Fact]
    public async Task Update_WithStaleVersion_ThrowsSagaConcurrencyException()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Started",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using var db2 = NewContext();
        var store = new EfCoreSagaSnapshotStore<TestState>(db2);
        var state = await store.FindAsync(correlationId);

        await Assert.ThrowsAsync<SagaConcurrencyException>(() => store.UpdateAsync(state!, expectedVersion: 5));
    }

    [Fact]
    public async Task Insert_DuplicateCorrelationId_ThrowsSagaAlreadyExistsException()
    {
        var correlationId = Guid.NewGuid();
        var makeState = () => new TestState { CorrelationId = correlationId, SagaType = "TestSaga", CurrentState = "Started", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };

        await using (var db = NewContext())
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(makeState());

        await using var db2 = NewContext();
        await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => new EfCoreSagaSnapshotStore<TestState>(db2).InsertAsync(makeState()));
    }

    [Fact]
    public async Task EventLog_AppendAndGetTimeline_PreservesOrderAndDetectsDuplicates()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var log = new EfCoreSagaEventLogStore(db);
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.SagaStarted, toState: "Started", messageId: "m1"));
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.MessageReceived, messageId: "m1"));
            await log.AppendAsync(SagaLogEntry.Create(correlationId, "TestSaga", SagaEntryType.StepSucceeded, fromState: "Started", toState: "Next", messageId: "m1"));
        }

        await using var db2 = NewContext();
        var log2 = new EfCoreSagaEventLogStore(db2);
        var timeline = await log2.GetTimelineAsync(correlationId);

        Assert.Equal(3, timeline.Count);
        Assert.Equal(SagaEntryType.SagaStarted, timeline[0].EntryType);
        Assert.Equal(SagaEntryType.StepSucceeded, timeline[2].EntryType);
        Assert.True(timeline[0].SequenceNumber < timeline[2].SequenceNumber);

        Assert.True(await log2.IsDuplicateAsync(correlationId, "m1"));
        Assert.False(await log2.IsDuplicateAsync(correlationId, "unknown"));
    }

    [Fact]
    public async Task Timeouts_ScheduleClaimAndCancel()
    {
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaTimeoutStore(db);
            await store.ScheduleAsync(correlationId, "TestSaga", "AwaitingPayment", now.AddMinutes(-1), CancellationToken.None);
        }

        await using (var db2 = NewContext())
        {
            var store2 = new EfCoreSagaTimeoutStore(db2);
            var due = await store2.ClaimDueAsync(now, batchSize: 10);
            var claimed = Assert.Single(due);
            Assert.Equal(correlationId, claimed.CorrelationId);
            Assert.Equal(SagaTimeoutStatus.Fired, claimed.Status);

            var dueAgain = await store2.ClaimDueAsync(now, batchSize: 10);
            Assert.Empty(dueAgain); // already fired, shouldn't be claimed twice
        }
    }

    [Fact]
    public async Task SummaryReader_ListsWithFiltering()
    {
        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "A", Status = SagaStatus.Running, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", CurrentState = "B", Status = SagaStatus.Failed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db2);

        var all = await reader.ListAsync(new SagaListFilter());
        Assert.Equal(2, all.TotalCount);

        var failedOnly = await reader.ListAsync(new SagaListFilter { Status = SagaStatus.Failed });
        Assert.Equal(1, failedOnly.TotalCount);
        Assert.Equal(SagaStatus.Failed, failedOnly.Items[0].Status);
    }

    [Fact]
    public async Task GetSagaTypesAsync_ReturnsDistinctTypesAcrossInstances()
    {
        // Regression test: GetSagaTypesAsync originally projected straight into the SagaTypeInfo
        // record inside Distinct()/OrderBy(), which Npgsql's provider could not translate to SQL —
        // only caught against a real Postgres, not SQLite. Keeping this test here (and the sibling
        // Postgres Testcontainers test) so both providers are exercised for this exact query shape.
        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", Kind = SagaKind.Orchestrated, CurrentState = "A", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "OrderSaga", Kind = SagaKind.Orchestrated, CurrentState = "B", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = Guid.NewGuid(), SagaType = "ShippingSaga", Kind = SagaKind.Choreographed, CurrentState = "A", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

        await using var db2 = NewContext();
        var types = await new EfCoreSagaSummaryReader(db2).GetSagaTypesAsync();

        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.SagaType == "OrderSaga" && t.Kind == SagaKind.Orchestrated);
        Assert.Contains(types, t => t.SagaType == "ShippingSaga" && t.Kind == SagaKind.Choreographed);
    }

    [Fact]
    public async Task GetDataJsonAsync_ReturnsSerializedState()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Started",
                OrderId = "ORD-9",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using var db2 = NewContext();
        var json = await new EfCoreSagaSummaryReader(db2).GetDataJsonAsync(correlationId);

        Assert.NotNull(json);
        Assert.Contains("\"OrderId\":\"ORD-9\"", json);
    }

    [Fact]
    public async Task ResetStateAsync_UpdatesColumnsAndKeepsEmbeddedJsonInSync()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "TestSaga",
                CurrentState = "Failed",
                Status = SagaStatus.Failed,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        await using (var db2 = NewContext())
            await new EfCoreSagaSummaryReader(db2).ResetStateAsync(correlationId, "Submitted", SagaStatus.Running);

        // The entity-level columns (read via ISagaSummaryReader) and the embedded DataJson (read via
        // ISagaSnapshotStore<TState>, which is what the orchestrator actually uses) must agree —
        // this is exactly the class of bug that once let Version drift out of sync with DataJson.
        await using var db3 = NewContext();
        var summary = await new EfCoreSagaSummaryReader(db3).GetAsync(correlationId);
        Assert.Equal("Submitted", summary!.CurrentState);
        Assert.Equal(SagaStatus.Running, summary.Status);

        await using var db4 = NewContext();
        var typedState = await new EfCoreSagaSnapshotStore<TestState>(db4).FindAsync(correlationId);
        Assert.Equal("Submitted", typedState!.CurrentState);
        Assert.Equal(SagaStatus.Running, typedState.Status);
    }
}
