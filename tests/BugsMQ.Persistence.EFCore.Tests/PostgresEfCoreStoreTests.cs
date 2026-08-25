using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace BugsMQ.Persistence.EFCore.Tests;

/// <summary>
/// Re-runs the provider-sensitive parts of the store suite against a real Postgres via
/// Testcontainers, not just SQLite. This is deliberately not a full duplicate of
/// <see cref="EfCoreStoreTests"/> — SQLite already covers general CRUD/concurrency logic; this class
/// exists specifically because SQLite's EF Core provider is more lenient than Npgsql about certain
/// LINQ shapes (GetSagaTypesAsync's Distinct-over-a-record-projection query translated fine on SQLite
/// but threw on Npgsql in production — see EfCoreSagaSummaryReader.GetSagaTypesAsync for the fix).
/// </summary>
public sealed class PostgresEfCoreStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<BugsMqDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _options = new DbContextOptionsBuilder<BugsMqDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql => npgsql.MigrationsAssembly("BugsMQ.Persistence.EFCore.Postgres"))
            .Options;

        await using var db = new BugsMqDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private BugsMqDbContext NewContext() => new(_options);

    [Fact]
    public async Task GetSagaTypesAsync_TranslatesAndReturnsDistinctTypes()
    {
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
        Assert.Contains(types, t => string.Equals(t.SagaType, "OrderSaga", StringComparison.Ordinal) && t.Kind == SagaKind.Orchestrated);
        Assert.Contains(types, t => string.Equals(t.SagaType, "ShippingSaga", StringComparison.Ordinal) && t.Kind == SagaKind.Choreographed);
    }

    [Fact]
    public async Task InsertUpdateAndList_RoundTripAgainstRealPostgres()
    {
        var correlationId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await new EfCoreSagaSnapshotStore<TestState>(db).InsertAsync(new TestState
            {
                CorrelationId = correlationId,
                SagaType = "OrderSaga",
                CurrentState = "Submitted",
                Status = SagaStatus.Running,
                OrderId = "ORD-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        TestState state;
        await using (var db2 = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db2);
            state = (await store.FindAsync("OrderSaga", correlationId))!;
            state.CurrentState = "Completed";
            state.Status = SagaStatus.Completed;
            await store.UpdateAsync(state, expectedVersion: 0);
        }

        await using var db3 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db3);
        var summary = await reader.GetAsync("OrderSaga", correlationId);

        Assert.NotNull(summary);
        Assert.Equal("Completed", summary.CurrentState);
        Assert.Equal(SagaStatus.Completed, summary.Status);
        Assert.Equal(1, summary.Version);

        var page = await reader.ListAsync(new SagaListFilter { Status = SagaStatus.Completed });
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_SortsByStatusAcrossTheWholeResultSet_AgainstRealPostgres()
    {
        var sagaType = $"SortSaga-{Guid.NewGuid():N}";
        var running = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var failed = Guid.NewGuid();

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaSnapshotStore<TestState>(db);
            // Inserted out of domain order deliberately, so a passing test can't be explained by insertion order.
            await store.InsertAsync(new TestState { CorrelationId = failed, SagaType = sagaType, CurrentState = "X", Status = SagaStatus.Failed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = running, SagaType = sagaType, CurrentState = "X", Status = SagaStatus.Running, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await store.InsertAsync(new TestState { CorrelationId = completed, SagaType = sagaType, CurrentState = "X", Status = SagaStatus.Completed, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

        await using var db2 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db2);

        // Split across two pages of the same sort to prove the ORDER BY is translated and applied to the
        // whole query (before Skip/Take), not just re-sorting whatever page happened to be requested.
        var page1 = await reader.ListAsync(new SagaListFilter { SagaType = sagaType, SortBy = SagaSortColumn.Status, Page = 1, PageSize = 2 });
        var page2 = await reader.ListAsync(new SagaListFilter { SagaType = sagaType, SortBy = SagaSortColumn.Status, Page = 2, PageSize = 2 });

        Assert.Equal(new[] { running, completed }, page1.Items.Select(s => s.CorrelationId));
        Assert.Equal(new[] { failed }, page2.Items.Select(s => s.CorrelationId));
    }

    [Fact]
    public async Task ClaimDueAsync_OrdersByDueDateAgainstRealPostgres()
    {
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaTimeoutStore(db);
            await store.ScheduleAsync("OrderSaga", late, "AwaitingPayment", now.AddSeconds(-1), CancellationToken.None);
            await store.ScheduleAsync("OrderSaga", early, "AwaitingInventory", now.AddSeconds(-5), CancellationToken.None);
        }

        await using var db2 = NewContext();
        var due = await new EfCoreSagaTimeoutStore(db2).ClaimDueAsync(now, batchSize: 10);

        Assert.Equal(2, due.Count);
        Assert.Equal(early, due[0].CorrelationId); // earliest due date claimed first
        Assert.Equal(late, due[1].CorrelationId);
    }

    [Fact]
    public async Task ClaimDueAsync_ConcurrentCallsNeverClaimTheSameTimeoutTwice()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var db = NewContext())
        {
            var store = new EfCoreSagaTimeoutStore(db);
            for (var i = 0; i < 20; i++)
                await store.ScheduleAsync("OrderSaga", Guid.NewGuid(), "AwaitingPayment", now.AddSeconds(-1), CancellationToken.None);
        }

        // Two dispatcher instances (separate DbContexts/connections) racing on the same 20 due rows —
        // FOR UPDATE SKIP LOCKED must partition them with no overlap, proving the atomic claim actually
        // works under concurrency rather than just compiling.
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var claimA = new EfCoreSagaTimeoutStore(dbA).ClaimDueAsync(now, batchSize: 20);
        var claimB = new EfCoreSagaTimeoutStore(dbB).ClaimDueAsync(now, batchSize: 20);
        var results = await Task.WhenAll(claimA, claimB);

        var claimedIds = results.SelectMany(r => r).Select(t => t.Id).ToList();
        Assert.Equal(20, claimedIds.Count);
        Assert.Equal(claimedIds.Count, claimedIds.Distinct().Count());
    }

    [Fact]
    public async Task ServiceTopologyStore_GetAllAsync_TranslatesAgainstRealPostgres()
    {
        await using (var db = NewContext())
        {
            var store = new EfCoreServiceTopologyStore(db);
            await store.RecordAsync("InventoryService", "ReserveInventory", "bugsmq.participant.inventory", DateTimeOffset.UtcNow);
            await store.RecordAsync("PaymentService", "ChargePayment", "bugsmq.participant.payment", DateTimeOffset.UtcNow);
        }

        await using var db2 = NewContext();
        var entries = await new EfCoreServiceTopologyStore(db2).GetAllAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => string.Equals(e.ServiceName, "InventoryService", StringComparison.Ordinal) && string.Equals(e.MessageType, "ReserveInventory", StringComparison.Ordinal));
        Assert.Contains(entries, e => string.Equals(e.ServiceName, "PaymentService", StringComparison.Ordinal) && string.Equals(e.MessageType, "ChargePayment", StringComparison.Ordinal));
    }
}
