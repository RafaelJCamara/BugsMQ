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
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        await using var db = new BugsMqDbContext(_options);
        await db.Database.EnsureCreatedAsync();
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
        Assert.Contains(types, t => t.SagaType == "OrderSaga" && t.Kind == SagaKind.Orchestrated);
        Assert.Contains(types, t => t.SagaType == "ShippingSaga" && t.Kind == SagaKind.Choreographed);
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
            state = (await store.FindAsync(correlationId))!;
            state.CurrentState = "Completed";
            state.Status = SagaStatus.Completed;
            await store.UpdateAsync(state, expectedVersion: 0);
        }

        await using var db3 = NewContext();
        var reader = new EfCoreSagaSummaryReader(db3);
        var summary = await reader.GetAsync(correlationId);

        Assert.NotNull(summary);
        Assert.Equal("Completed", summary!.CurrentState);
        Assert.Equal(SagaStatus.Completed, summary.Status);
        Assert.Equal(1, summary.Version);

        var page = await reader.ListAsync(new SagaListFilter { Status = SagaStatus.Completed });
        Assert.Equal(1, page.TotalCount);
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
            await store.ScheduleAsync(late, "OrderSaga", "AwaitingPayment", now.AddSeconds(-1), CancellationToken.None);
            await store.ScheduleAsync(early, "OrderSaga", "AwaitingInventory", now.AddSeconds(-5), CancellationToken.None);
        }

        await using var db2 = NewContext();
        var due = await new EfCoreSagaTimeoutStore(db2).ClaimDueAsync(now, batchSize: 10);

        Assert.Equal(2, due.Count);
        Assert.Equal(early, due[0].CorrelationId); // earliest due date claimed first
        Assert.Equal(late, due[1].CorrelationId);
    }
}
