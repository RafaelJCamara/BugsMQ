using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Core.Tests;

public sealed record BeginTracingTest(string OrderId);

// Empty records are intentional -- see TestOrderSaga.cs's own precedent: correlation in these tests
// is always carried by the explicit MessageEnvelope, so these zero-payload markers just need a
// distinct CLR type for routing/deserialization.
#pragma warning disable S2094
public sealed record TracingTestAdvance;
public sealed record TracingTestBoom;
#pragma warning restore S2094

public sealed class TracingTestSagaState : SagaState
{
    public string? OrderId { get; set; }
}

/// <summary>
/// A minimal, self-contained saga for production-readiness.md §6/§8.18's tracing/metrics tests --
/// deliberately its own type (distinct SagaType/instrument tags from every other fixture in this
/// project) so a <see cref="System.Diagnostics.ActivityListener"/> or
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> scoped to it can't pick up noise from
/// unrelated tests running concurrently elsewhere in this assembly. No step here ever calls
/// ctx.Publish/SendAsync -- these tests care about the consumer span and the SagaDuration/status
/// wiring, not the producer span, so keeping every step side-effect-free keeps each test's captured
/// activity list to exactly the span(s) it's asserting on.
/// </summary>
public sealed class TracingTestSaga : OrchestratedSagaDefinition<TracingTestSagaState>
{
    public State<TracingTestSagaState> Start { get; }
    public State<TracingTestSagaState> Awaiting { get; }
    public State<TracingTestSagaState> Done { get; }
    public State<TracingTestSagaState> Boomed { get; }

    public TracingTestSaga()
    {
        Start = InitialState(nameof(Start));
        Awaiting = State(nameof(Awaiting));
        Done = State(nameof(Done));
        Boomed = State(nameof(Boomed));

        During(Start)
            .When<BeginTracingTest>()
                .Then((ctx, m) => ctx.Saga.OrderId = m.OrderId)
                .TransitionTo(Awaiting);

        During(Awaiting)
            .When<TracingTestAdvance>()
                .TransitionTo(Done)
                .Finalize(SagaStatus.Completed)
            .When<TracingTestBoom>()
                .Then((_, _) => throw new InvalidOperationException("simulated step failure"))
                .TransitionTo(Boomed);

        WithTimeout(Awaiting, TimeSpan.FromMinutes(5), t => t.TransitionTo(Done).Finalize(SagaStatus.TimedOut));
    }
}
