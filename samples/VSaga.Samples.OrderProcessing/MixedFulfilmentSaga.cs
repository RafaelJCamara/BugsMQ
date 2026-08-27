using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;
using VSaga.Http;
using VSaga.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Configuration;

namespace VSaga.Samples.OrderProcessing;

/// <summary>
/// Loopback-only results of the REST calls below -- produced and consumed entirely within one saga
/// instance's own round trip, never published by another service, so they live here rather than in the
/// cross-service Contracts project, matching <c>LoyaltyTierResolved</c>'s precedent. Registered under
/// <see cref="MixedFulfilmentSaga.AwaitingAuthorization"/>/<see cref="MixedFulfilmentSaga.Voiding"/>,
/// never <see cref="MixedFulfilmentSaga.Start"/> -- a message type registered under the saga's
/// InitialState also counts as capable of initiating a fresh instance (see
/// <c>OrchestratedSagaDefinition.CanInitiate</c>), which a pure loopback reply has no business doing.
/// </summary>
public sealed record PaymentAuthorized(string AuthorizationId);

public sealed record PaymentVoided(DateTimeOffset VoidedAt);

public sealed class MixedFulfilmentSagaState : SagaState
{
    public string? OrderId { get; set; }

    public decimal Amount { get; set; }

    public bool Declined { get; set; }

    public string? AuthorizationId { get; set; }

    public bool StockReserved { get; set; }

    public bool VoidFailed { get; set; }

    public DateTimeOffset? VoidedAtUtc { get; set; }
}

/// <summary>
/// docs/mixed-sagas.md: a saga that drives a RabbitMQ participant and a REST participant side by side --
/// authorizes payment over REST, then reserves stock over the broker -- and whose compensation unwinds
/// both kinds of hop: on stock failure or timeout, it releases the stock over the broker and voids the
/// authorization over REST, waiting for the void to confirm before calling itself Failed. Both hops are
/// visible on the Saga Map, proven against a live docker compose stack.
///
/// <para>
/// The flow is deliberately sequential, not a fan-out: a declined authorization must not be able to
/// strand a stock reservation that nothing will release. Money is authorized first, stock is reserved
/// only once authorization succeeded, and the saga does not declare itself Failed until the reversal is
/// actually confirmed -- <see cref="Voiding"/> exists because of the rule that a compensating loopback
/// may use a loopback outcome only if the state it transitions into handles that reply and is not itself
/// terminal (a compensating call that transitioned straight into a Finalize step would let the drained
/// loopback resurrect an already-terminal saga -- RunStepAsync unconditionally flips Status from Failed
/// back to Running on any redelivery).
/// </para>
/// </summary>
public sealed class MixedFulfilmentSaga : OrchestratedSagaDefinition<MixedFulfilmentSagaState>
{
    public State<MixedFulfilmentSagaState> Start { get; }
    public State<MixedFulfilmentSagaState> AwaitingAuthorization { get; }
    public State<MixedFulfilmentSagaState> AwaitingStock { get; }
    public State<MixedFulfilmentSagaState> Voiding { get; }
    public State<MixedFulfilmentSagaState> Fulfilled { get; }
    public State<MixedFulfilmentSagaState> Failed { get; }

    /// <summary>Matches OrderSaga's own ReplyTimeout: generous against the participants' 150-500ms simulated work and chaos mode's 4s inbound delay ceiling.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    private readonly string _authorizeUrl;
    private readonly string _voidUrl;

    public MixedFulfilmentSaga(IConfiguration configuration)
    {
        // Same same-process fallback pattern as LoyaltyLookupSaga.cs.
#pragma warning disable S1075 // the fallback is a same-process default, not a hardcoded remote dependency
        _authorizeUrl = configuration["Payments:AuthorizeUrl"] ?? "http://localhost:8080/payments/authorize";
        _voidUrl = configuration["Payments:VoidUrl"] ?? "http://localhost:8080/payments/void";
#pragma warning restore S1075

        Start = InitialState(nameof(Start));
        AwaitingAuthorization = State(nameof(AwaitingAuthorization));
        AwaitingStock = State(nameof(AwaitingStock));
        Voiding = State(nameof(Voiding));
        Fulfilled = State(nameof(Fulfilled));
        Failed = State(nameof(Failed));

        ConfigureHappyPath();
        ConfigureCompensation();
    }

    /// <summary>Authorize over REST, reserve stock over the broker, fulfil. Split out of the constructor purely for length, same reason as OrderSaga.ConfigureGathering.</summary>
    private void ConfigureHappyPath()
    {
        During(Start)
            .When<FulfilmentRequested>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => { ctx.Saga.OrderId = m.OrderId; ctx.Saga.Amount = m.Amount; })
                .CallHttp(h => h                                    // REST participant
                    .Post(_authorizeUrl)
                    .Body((ctx, m) => new { m.OrderId, m.Amount })
                    .OnSuccess<PaymentAuthorized>()                 // loopback
                    .OnStatus(402).Then(s => s.Declined = true)     // inline
                    .OnFailure(s => s.Declined = true))
                .TransitionTo(s => s.Declined ? Failed : AwaitingAuthorization)
                .Finalize(s => s.Declined ? SagaStatus.Failed : (SagaStatus?)null);
                // A decline needs no unwind: nothing has been reserved and there is no authorization to void.

        During(AwaitingAuthorization)
            .When<PaymentAuthorized>()                              // loopback reply
                .Then((ctx, m) => ctx.Saga.AuthorizationId = m.AuthorizationId)
                .Publish((ctx, _) => new ReserveStock(ctx.CorrelationId, ctx.Saga.OrderId!))   // broker hop
                .TransitionTo(AwaitingStock);

        During(AwaitingStock)
            .When<StockReserved>()
                .Then((ctx, _) => ctx.Saga.StockReserved = true)
                .TransitionTo(Fulfilled).Finalize(SagaStatus.Completed)
            .When<StockUnavailable>()
                .Compensate()
                .TransitionTo(Voiding);        // deliberately NOT terminal yet -- see class doc comment

        During(Voiding)
            .When<PaymentVoided>()                                  // loopback from the compensating REST call
                .Then((ctx, m) => ctx.Saga.VoidedAtUtc = m.VoidedAt)
                .TransitionTo(Failed).Finalize(SagaStatus.Failed);
    }

    /// <summary>How a stock failure or timeout unwinds -- the point of the feature: it unwinds both kinds of hop. Split out of the constructor purely for length; every rule here is about undoing work, not driving the order forward.</summary>
    private void ConfigureCompensation()
    {
        Compensate(AwaitingStock, async (ctx, ct) =>
        {
            // Sequential awaits, never Task.WhenAll: ctx.PublishAsync and ctx.CallHttpAsync share this
            // saga's one SagaContext and the single DbContext behind its event log, which is only safe
            // one operation at a time -- the same reason OrderSaga's own compensation publishes are
            // sequential.
            await ctx.PublishAsync(new ReleaseStock(ctx.CorrelationId, ctx.Saga.OrderId!), ct);   // broker

            await ctx.CallHttpAsync(h => h                                                        // REST
                .Post(_voidUrl)
                .Body(new { ctx.Saga.AuthorizationId })
                .OnSuccess<PaymentVoided>()            // loopback -- the saga waits for confirmation
                .OnFailure(s => s.VoidFailed = true), ct);
        });

        WithTimeout(AwaitingStock, ReplyTimeout, t => t.Compensate().TransitionTo(Voiding));

        // Backstop: if the void itself never confirms (OnFailure fired, so no loopback), don't hang forever.
        WithTimeout(Voiding, ReplyTimeout, t => t.TransitionTo(Failed).Finalize(SagaStatus.TimedOut));
    }
}
