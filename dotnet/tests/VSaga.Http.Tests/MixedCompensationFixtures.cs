using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Http.Tests;

public sealed record BeginMixedFlow(string RequestId);

// Empty record is intentional: correlation is carried by the message envelope in tests, so this
// zero-payload marker just needs a distinct CLR type for routing -- see TestOrderSaga.cs's own precedent.
#pragma warning disable S2094
/// <summary>Triggers compensation -- stands in for a downstream broker failure in a real mixed saga.</summary>
public sealed record MixedFlowFailed;
#pragma warning restore S2094

/// <summary>The broker half of the compensation -- stands in for OrderProcessing's ReleaseStock.</summary>
public sealed record ReleaseHold(string RequestId);

/// <summary>The REST half's loopback reply -- stands in for PaymentVoided.</summary>
public sealed record MixedCallSucceeded(string Body);

public sealed class MixedCompensationTestState : SagaState
{
    public string? RequestId { get; set; }
}

/// <summary>
/// docs/mixed-sagas.md §4/§9: a compensation delegate performing a broker publish and a
/// <c>ctx.CallHttpAsync</c> in order, asserting both happened -- the two-hop shape a mixed saga's own
/// compensation needs, at the smallest scale that exercises it. <c>Reversing</c> is deliberately non-
/// terminal (§8's rule): the loopback from <c>ctx.CallHttpAsync</c> is what finally drives the saga to
/// its terminal status, not the compensation step itself.
/// </summary>
public sealed class MixedCompensationTestSaga : OrchestratedSagaDefinition<MixedCompensationTestState>
{
    public State<MixedCompensationTestState> Start { get; }
    public State<MixedCompensationTestState> Active { get; }
    public State<MixedCompensationTestState> Reversing { get; }
    public State<MixedCompensationTestState> Reversed { get; }

    public MixedCompensationTestSaga()
    {
        Start = InitialState(nameof(Start));
        Active = State(nameof(Active));
        Reversing = State(nameof(Reversing));
        Reversed = State(nameof(Reversed));

        During(Start)
            .When<BeginMixedFlow>()
                .Then((ctx, m) => ctx.Saga.RequestId = m.RequestId)
                .TransitionTo(Active);

        During(Active)
            .When<MixedFlowFailed>()
                .Compensate()
                .TransitionTo(Reversing);

        During(Reversing)
            .When<MixedCallSucceeded>()
                .TransitionTo(Reversed)
                .Finalize(SagaStatus.Failed);

        Compensate(Active, async (ctx, ct) =>
        {
            await ctx.PublishAsync(new ReleaseHold(ctx.Saga.RequestId!), ct);

            await ctx.CallHttpAsync(h => h
                .Post("https://call-target.test/void-mixed")
                .Body(new { ctx.Saga.RequestId })
                .OnSuccess<MixedCallSucceeded>(), ct);
        });
    }
}
