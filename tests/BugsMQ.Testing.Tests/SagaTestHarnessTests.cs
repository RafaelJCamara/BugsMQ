using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Testing.Tests;

public sealed class SagaTestHarnessTests
{
    [Fact]
    public async Task GivenWhenThen_HappyPath_TransitionsAndPublishes()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-1"));

        var state = await harness.AssertStateAsync(harness.Saga.AwaitingShipment);
        Assert.Equal("ORD-1", state.OrderId);

        await harness.WhenAsync(new ShipmentConfirmed());

        await harness.AssertStateAsync(harness.Saga.Shipped);
        await harness.AssertStatusAsync(SagaStatus.Completed);
    }

    [Fact]
    public async Task GivenWhenThen_FailurePath_RunsCompensation()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-2"));
        await harness.WhenAsync(new ShipmentFailed());

        await harness.AssertStateAsync(harness.Saga.Failed);
        await harness.AssertStatusAsync(SagaStatus.Failed);
        harness.AssertPublished<ReleaseHold>();
    }

    [Fact]
    public async Task Timeout_FiresDeterministicallyWithoutRealWaiting()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new OrderPlaced("ORD-3"));

        await harness.AssertStateAsync(harness.Saga.AwaitingShipment);

        await harness.AdvanceTimeByAsync(TimeSpan.FromMinutes(31));

        await harness.AssertStateAsync(harness.Saga.Failed);
        await harness.AssertStatusAsync(SagaStatus.TimedOut);
    }

    [Fact]
    public async Task NonInitiatingMessage_DoesNotCreateASaga()
    {
        await using var harness = new SagaTestHarness<DemoSaga, DemoSagaState>();

        await harness.Given(Guid.NewGuid())
            .WhenAsync(new ShipmentConfirmed());

        await harness.AssertNoSagaCreatedAsync();
    }
}
