using BugsMQ.Abstractions.Sagas;
using BugsMQ.Core.Dsl;

namespace BugsMQ.Core.Tests;

public sealed record FlakyWithPolicy;
public sealed record AlwaysFailsWithPolicy;

public sealed class TestOrderSagaState : SagaState
{
    public string? OrderId { get; set; }

    public decimal Amount { get; set; }

    public int InventoryAttempts { get; set; }
}

public sealed record OrderSubmitted(string OrderId, decimal Amount);
public sealed record ReserveInventory(Guid CorrelationId, string OrderId);
public sealed record InventoryReserved;
public sealed record InventoryReservationFailed;
public sealed record ChargePayment(Guid CorrelationId, decimal Amount);
public sealed record PaymentCharged;
public sealed record PaymentFailed;
public sealed record ReleaseInventory(Guid CorrelationId);
public sealed record FlakyStep;

public sealed class TestOrderSaga : OrchestratedSagaDefinition<TestOrderSagaState>
{
    public readonly State<TestOrderSagaState> Submitted = default!;
    public readonly State<TestOrderSagaState> AwaitingInventory = default!;
    public readonly State<TestOrderSagaState> AwaitingPayment = default!;
    public readonly State<TestOrderSagaState> Completed = default!;
    public readonly State<TestOrderSagaState> Failed = default!;

    /// <summary>Shared across replays so a test can make a step fail once then succeed on manual retry.</summary>
    public int FlakyStepAttempts;

    /// <summary>Attempt counter for the in-process, step-level RetryPolicy tests (distinct from manual whole-saga retry above).</summary>
    public int FlakyWithPolicyAttempts;

    public int AlwaysFailsWithPolicyAttempts;

    public TestOrderSaga()
    {
        Submitted = InitialState(nameof(Submitted));
        AwaitingInventory = State(nameof(AwaitingInventory));
        AwaitingPayment = State(nameof(AwaitingPayment));
        Completed = State(nameof(Completed));
        Failed = State(nameof(Failed));

        During(Submitted)
            .When<OrderSubmitted>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.Saga.Amount = m.Amount)
                .Publish((ctx, m) => new ReserveInventory(ctx.CorrelationId, m.OrderId))
                .TransitionTo(AwaitingInventory);

        During(AwaitingInventory)
            .When<InventoryReserved>()
                .Publish((ctx, _) => new ChargePayment(ctx.CorrelationId, ctx.Saga.Amount))
                .TransitionTo(AwaitingPayment)
            .When<InventoryReservationFailed>()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed)
            .When<FlakyStep>()
                .Then((_, _) =>
                {
                    FlakyStepAttempts++;
                    if (FlakyStepAttempts == 1)
                        throw new InvalidOperationException("simulated transient failure");
                })
                .TransitionTo(AwaitingPayment)
            .When<FlakyWithPolicy>()
                .Then((_, _) =>
                {
                    FlakyWithPolicyAttempts++;
                    if (FlakyWithPolicyAttempts < 3)
                        throw new InvalidOperationException("transient failure, should be retried in-process");
                })
                .Retry(RetryPolicy.Exponential(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1)))
                .TransitionTo(AwaitingPayment)
            .When<AlwaysFailsWithPolicy>()
                .Then((_, _) =>
                {
                    AlwaysFailsWithPolicyAttempts++;
                    throw new InvalidOperationException("always fails");
                })
                .Retry(RetryPolicy.Exponential(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(1)))
                .TransitionTo(AwaitingPayment);

        During(AwaitingPayment)
            .When<PaymentCharged>()
                .TransitionTo(Completed)
                .Finalize(SagaStatus.Completed)
            .When<PaymentFailed>()
                .Compensate()
                .TransitionTo(Failed)
                .Finalize(SagaStatus.Failed);

        Compensate(AwaitingInventory, (ctx, ct) => ctx.PublishAsync(new ReleaseInventory(ctx.CorrelationId), ct));

        WithTimeout(AwaitingPayment, TimeSpan.FromMinutes(5), t => t.TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
    }
}
