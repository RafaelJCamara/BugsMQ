using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;
using VSaga.Http;
using VSaga.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Configuration;

namespace VSaga.Samples.OrderProcessing;

/// <summary>
/// Loopback-only result of the <c>.CallHttp</c> call below -- produced and consumed entirely within one
/// saga instance's own round trip, never published by another service, so it lives here rather than in
/// the cross-service Contracts project.
/// </summary>
public sealed record LoyaltyTierResolved(string Tier);

public sealed class LoyaltyLookupSagaState : SagaState
{
    public string? OrderId { get; set; }

    public int Points { get; set; }

    public string? Tier { get; set; }

    public bool LookupFailed { get; set; }
}

/// <summary>
/// Live-verification vehicle for docs/design/http-based-sagas.md §5.2/§5.3's <c>.CallHttp(...)</c>: reacts to
/// the same <see cref="LoyaltyPointsAwarded"/> event <see cref="PostShipmentChoreography"/> already
/// tracks (a second, independent subscriber -- ordinary fan-out, nothing shared between the two), and
/// makes a real synchronous REST call to this same process's own <c>/loyalty/lookup</c> endpoint (an
/// ordinary Minimal API route, no vSaga awareness at all -- see Program.cs) instead of publishing
/// another command onto the broker.
///
/// <para>
/// Demonstrates both result shapes from the design doc in one step: a 2xx response loops back as
/// <see cref="LoyaltyTierResolved"/> via <c>.OnSuccess&lt;TOut&gt;()</c> (message loopback, driving a
/// second step once it re-enters), while the endpoint's own simulated failure rate is handled inline
/// via <c>.OnFailure(Action&lt;TState&gt;)</c> -- no loopback, so the very same step's computed
/// <c>.TransitionTo</c>/<c>.Finalize</c> selectors decide the outcome immediately.
/// </para>
/// </summary>
public sealed class LoyaltyLookupSaga : OrchestratedSagaDefinition<LoyaltyLookupSagaState>
{
    public State<LoyaltyLookupSagaState> Start { get; }
    public State<LoyaltyLookupSagaState> AwaitingLookup { get; }
    public State<LoyaltyLookupSagaState> Done { get; }

    public LoyaltyLookupSaga(IConfiguration configuration)
    {
        // Configurable rather than a literal, so a future overlay could point this at a real separate
        // service — defaults to this same process's own /loyalty/lookup endpoint (Program.cs), reachable
        // at a known port because docker-compose.yml now sets ASPNETCORE_URLS for order-processing.
#pragma warning disable S1075 // the fallback is a same-process default, not a hardcoded remote dependency
        var lookupUrl = configuration["Loyalty:LookupUrl"] ?? "http://localhost:8080/loyalty/lookup";
#pragma warning restore S1075

        Start = InitialState(nameof(Start));
        AwaitingLookup = State(nameof(AwaitingLookup));
        Done = State(nameof(Done));

        // LoyaltyTierResolved is registered under AwaitingLookup, never Start -- exactly the same reason
        // OrderSaga keeps its own follow-up messages off its initial state: a type registered under
        // InitialStateName also counts as capable of *initiating* a fresh instance (CanInitiate reads
        // StepsByState[InitialStateName].Keys), which a pure loopback reply -- only ever published under
        // an already-existing correlation id -- has no business doing.
        During(Start)
            .When<LoyaltyPointsAwarded>()
                .CorrelateBy(m => m.OrderId, s => s.OrderId)
                .Then((ctx, m) => ctx.Saga.Points = m.Points)
                .CallHttp(h => h
                    .Post(lookupUrl)
                    .Body((ctx, m) => new { ctx.Saga.OrderId, ctx.Saga.Points })
                    .OnSuccess<LoyaltyTierResolved>()
                    .OnFailure(s => s.LookupFailed = true))
                .TransitionTo(s => s.LookupFailed ? Done : AwaitingLookup)
                .Finalize(s => s.LookupFailed ? SagaStatus.Failed : (SagaStatus?)null);

        During(AwaitingLookup)
            .When<LoyaltyTierResolved>()
                .Then((ctx, m) => ctx.Saga.Tier = m.Tier)
                .TransitionTo(Done)
                .Finalize(SagaStatus.Completed);
    }
}
