using VSaga.Abstractions.Persistence;
using VSaga.Persistence.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Core.Tests;

/// <summary>
/// Direct coverage of <see cref="ISagaOutboxStore"/>'s in-memory implementation, independent of
/// SagaOrchestrator's own use of it (production-readiness §8.8) -- these exercise the store contract's
/// full id/claim/dispatch shape on its own rather than only incidentally through a saga run.
/// </summary>
public sealed class InMemoryOutboxStoreTests
{
    private static ISagaOutboxStore NewStore()
    {
        var services = new ServiceCollection();
        services.AddVSagaInMemoryPersistence();
        return services.BuildServiceProvider().GetRequiredService<ISagaOutboxStore>();
    }

    [Fact]
    public async Task EnqueueRoundTripsEveryFieldAndClaimNeverReturnsItTwice()
    {
        var store = NewStore();
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["x-vsaga-source-service"] = "OrderSaga" };
        var body = "{\"OrderId\":\"ORD-1\"}"u8.ToArray();

        await store.EnqueueAsync("OrderSaga", correlationId, "m1", "InventoryReserved", body, destination: null, headers, now.AddMinutes(-1));

        var claimed = Assert.Single(await store.ClaimPendingAsync(now, batchSize: 10));
        Assert.Equal(correlationId, claimed.CorrelationId);
        Assert.Equal("OrderSaga", claimed.SagaType);
        Assert.Equal("m1", claimed.MessageId);
        Assert.Equal("InventoryReserved", claimed.MessageTypeName);
        Assert.Equal(body, claimed.Body.ToArray());
        Assert.Null(claimed.Destination);
        Assert.Equal("OrderSaga", claimed.Headers["x-vsaga-source-service"]);
        Assert.Equal(SagaOutboxStatus.Dispatched, claimed.Status);

        Assert.Empty(await store.ClaimPendingAsync(now, batchSize: 10)); // already dispatched, shouldn't be claimed twice
    }

    [Fact]
    public async Task MarkDispatchedDirectly_ExcludesItFromTheRecoveryClaim()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        await store.EnqueueAsync("OrderSaga", Guid.NewGuid(), "m2", "ChargePayment", "{}"u8.ToArray(),
            destination: "payments", new Dictionary<string, string>(StringComparer.Ordinal), now.AddMinutes(-1));

        var pendingRightNow = await store.ClaimPendingAsync(now.AddMinutes(-10), batchSize: 10);
        Assert.Empty(pendingRightNow); // not yet past its grace period

        // The inline drain's path: mark dispatched directly by id, right after sending it synchronously.
        // Never goes through ClaimPendingAsync -- a fresh store's first enqueued row is always id 1.
        await store.MarkDispatchedAsync(1);

        Assert.Empty(await store.ClaimPendingAsync(now, batchSize: 10));
    }

    [Fact]
    public async Task ClaimPendingAsync_RespectsBatchSize()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            await store.EnqueueAsync("OrderSaga", Guid.NewGuid(), $"m{i}", "InventoryReserved", "{}"u8.ToArray(),
                destination: null, new Dictionary<string, string>(StringComparer.Ordinal), now.AddMinutes(-1));
        }

        var firstBatch = await store.ClaimPendingAsync(now, batchSize: 3);
        Assert.Equal(3, firstBatch.Count);

        var secondBatch = await store.ClaimPendingAsync(now, batchSize: 3);
        Assert.Equal(2, secondBatch.Count); // the remaining 2, not reclaiming the first batch's 3
    }
}
