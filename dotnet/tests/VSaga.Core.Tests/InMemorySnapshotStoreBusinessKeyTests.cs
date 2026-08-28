using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Persistence.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Core.Tests;

/// <summary>
/// Direct coverage of the in-memory snapshot store's BusinessKey reservation/lookup (production-
/// readiness §5.2, item 12) -- the same case list as EfCoreStoreTests' BusinessKey section, adapted to
/// this provider's API, so the guarantee is pinned on both providers rather than just the one under
/// test by default. Independent of SagaOrchestrator's own use of BusinessKey, which doesn't exist yet
/// (items 13/14): nothing here ever sets it via a saga run, only directly on the state.
/// </summary>
public sealed class InMemorySnapshotStoreBusinessKeyTests
{
    private sealed class TestState : SagaState
    {
        public string? OrderId { get; set; }
    }

    private static ISagaSnapshotStore<TestState> NewStore()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryPersistence();
        return services.BuildServiceProvider().GetRequiredService<ISagaSnapshotStore<TestState>>();
    }

    private static TestState NewState(Guid correlationId, string sagaType, string currentState, string? businessKey = null) => new()
    {
        CorrelationId = correlationId,
        SagaType = sagaType,
        CurrentState = currentState,
        Status = SagaStatus.Running,
        BusinessKey = businessKey,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Insert_TwoInstancesOfTheSameSagaTypeWithNullBusinessKey_BothSucceed()
    {
        // THE most important test in this class: every one of the 231+ existing tests never sets
        // BusinessKey, so it stays null on every insert. The reservation dictionary must only ever be
        // consulted when BusinessKey is non-null -- otherwise every saga in this codebase today would
        // collide on its first second instance.
        var store = NewStore();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await store.InsertAsync(NewState(firstId, "TestSaga", "Started"));
        await store.InsertAsync(NewState(secondId, "TestSaga", "Started"));

        Assert.NotNull(await store.FindAsync("TestSaga", firstId));
        Assert.NotNull(await store.FindAsync("TestSaga", secondId));
    }

    [Fact]
    public async Task InsertWithBusinessKey_ThenFindByBusinessKeyAsync_ReturnsTheRightInstance()
    {
        var store = NewStore();
        var correlationId = Guid.NewGuid();

        await store.InsertAsync(new TestState
        {
            CorrelationId = correlationId,
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            Status = SagaStatus.Running,
            OrderId = "ORD-BK-1",
            BusinessKey = "ORD-BK-1",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var found = await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-1");

        Assert.NotNull(found);
        Assert.Equal(correlationId, found.CorrelationId);
        Assert.Equal("ORD-BK-1", found.OrderId);
        Assert.Equal("Submitted", found.CurrentState);
    }

    [Fact]
    public async Task FindByBusinessKeyAsync_ForAnUnclaimedKey_ReturnsNull()
    {
        var store = NewStore();
        await store.InsertAsync(NewState(Guid.NewGuid(), "OrderSaga", "Submitted", businessKey: "ORD-BK-2"));

        Assert.Null(await store.FindByBusinessKeyAsync("OrderSaga", "no-such-key"));
    }

    [Fact]
    public async Task Insert_SameSagaTypeAndBusinessKeyAsAnExistingInstance_ThrowsSagaAlreadyExistsException()
    {
        var store = NewStore();
        await store.InsertAsync(NewState(Guid.NewGuid(), "OrderSaga", "Submitted", businessKey: "ORD-BK-3"));

        await Assert.ThrowsAsync<SagaAlreadyExistsException>(() =>
            store.InsertAsync(NewState(Guid.NewGuid(), "OrderSaga", "Submitted", businessKey: "ORD-BK-3")));
    }

    [Fact]
    public async Task Insert_SameBusinessKeyUnderADifferentSagaType_Succeeds()
    {
        // The reservation is scoped per SagaType, matching the composite (SagaType, CorrelationId)
        // key's own SagaType-first precedent.
        var store = NewStore();

        await store.InsertAsync(NewState(Guid.NewGuid(), "OrderSaga", "Submitted", businessKey: "ORD-BK-4"));
        await store.InsertAsync(NewState(Guid.NewGuid(), "ShippingChoreography", "Tracking", businessKey: "ORD-BK-4"));

        Assert.NotNull(await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-4"));
        Assert.NotNull(await store.FindByBusinessKeyAsync("ShippingChoreography", "ORD-BK-4"));
    }

    [Fact]
    public async Task Update_KeepsBusinessKeyResolvableAfterAnUnrelatedFieldChanges()
    {
        var store = NewStore();
        var correlationId = Guid.NewGuid();

        await store.InsertAsync(new TestState
        {
            CorrelationId = correlationId,
            SagaType = "OrderSaga",
            CurrentState = "Submitted",
            OrderId = "ORD-BK-5",
            BusinessKey = "ORD-BK-5",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var state = await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-5");
        state!.CurrentState = "Completed";
        await store.UpdateAsync(state, expectedVersion: 0);

        var reloaded = await store.FindByBusinessKeyAsync("OrderSaga", "ORD-BK-5");
        Assert.NotNull(reloaded);
        Assert.Equal(correlationId, reloaded.CorrelationId);
        Assert.Equal("Completed", reloaded.CurrentState);
        Assert.Equal(1, reloaded.Version);
    }

    // Regression coverage for the gap an adversarial review of item 12 found: BusinessKey has a plain
    // public setter (nothing prevents a caller from changing it after Insert), and the reservation
    // dictionary used to be populated once at Insert and never refreshed by Update -- so DataJson (the
    // real source of truth) and the reservation index would silently disagree after any BusinessKey
    // change. FindByBusinessKeyAsync must stay a correct index over the current BusinessKey after
    // UpdateAsync moves it, exactly as StoredSnapshot's promoted column is required to.
    [Fact]
    public async Task Update_ChangingBusinessKey_MovesTheReservationSoOnlyTheNewKeyResolves()
    {
        var store = NewStore();
        var correlationId = Guid.NewGuid();

        await store.InsertAsync(NewState(correlationId, "OrderSaga", "Submitted", businessKey: "OLD-KEY"));

        var state = await store.FindByBusinessKeyAsync("OrderSaga", "OLD-KEY");
        state!.BusinessKey = "NEW-KEY";
        await store.UpdateAsync(state, expectedVersion: 0);

        Assert.Null(await store.FindByBusinessKeyAsync("OrderSaga", "OLD-KEY"));

        var found = await store.FindByBusinessKeyAsync("OrderSaga", "NEW-KEY");
        Assert.NotNull(found);
        Assert.Equal(correlationId, found.CorrelationId);
        Assert.Equal("NEW-KEY", found.BusinessKey);

        // FindAsync (by correlation id, deserializing DataJson directly) must agree with the
        // reservation-indexed lookup -- the two must never disagree, which is the whole point of §5.2.
        var byCorrelationId = await store.FindAsync("OrderSaga", correlationId);
        Assert.Equal("NEW-KEY", byCorrelationId!.BusinessKey);
    }

    [Fact]
    public async Task Update_ChangingBusinessKeyToOneAlreadyClaimedByAnotherInstance_ThrowsSagaAlreadyExistsException()
    {
        var store = NewStore();
        await store.InsertAsync(NewState(Guid.NewGuid(), "OrderSaga", "Submitted", businessKey: "TAKEN-KEY"));

        var moverId = Guid.NewGuid();
        await store.InsertAsync(NewState(moverId, "OrderSaga", "Submitted", businessKey: "MOVER-KEY"));
        var mover = await store.FindByBusinessKeyAsync("OrderSaga", "MOVER-KEY");
        mover!.BusinessKey = "TAKEN-KEY";

        await Assert.ThrowsAsync<SagaAlreadyExistsException>(() => store.UpdateAsync(mover, expectedVersion: 0));

        // The rejected update must not have moved the mover's reservation off its original key, and
        // must not have clobbered the incumbent holding TAKEN-KEY.
        var stillAtMoverKey = await store.FindByBusinessKeyAsync("OrderSaga", "MOVER-KEY");
        Assert.NotNull(stillAtMoverKey);
        Assert.Equal(moverId, stillAtMoverKey.CorrelationId);

        var incumbent = await store.FindByBusinessKeyAsync("OrderSaga", "TAKEN-KEY");
        Assert.NotNull(incumbent);
        Assert.NotEqual(moverId, incumbent.CorrelationId);
    }
}
