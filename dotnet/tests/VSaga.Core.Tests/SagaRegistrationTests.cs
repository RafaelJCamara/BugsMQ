using VSaga.Abstractions.Sagas;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// The engine resolves a saga's definition as <c>ISagaDefinition&lt;TState&gt;</c>, so two definitions
/// sharing one state class cannot both work — the second registration wins and the first saga simply
/// never runs, with no error and its messages going nowhere.
///
/// <para>
/// Found while building the fan-out tests, where a second saga was given the same state class as the
/// first and silently never received anything. Now rejected at registration, since the runtime symptom
/// is an inexplicably missing saga rather than anything pointing at the cause.
/// </para>
/// </summary>
public sealed class SagaRegistrationTests
{
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddVSagaInMemoryPersistence();
        services.AddVSagaInMemoryTransport();
        return services;
    }

    [Fact]
    public void RegisteringTwoSagasWithTheSameStateTypeThrows()
    {
        var services = NewServices();

        var ex = Assert.Throws<SagaDefinitionException>(() =>
            services.AddVSagaEngine(o => o
                .AddSaga<TestParallelFulfilmentSaga, ParallelFulfilmentState>()
                .AddSaga<AnotherSagaSharingAState, ParallelFulfilmentState>()));

        Assert.Contains(nameof(ParallelFulfilmentState), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AnotherSagaSharingAState), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteringSagasWithDistinctStateTypesSucceeds()
    {
        var services = NewServices();

        services.AddVSagaEngine(o => o
            .AddSaga<TestParallelFulfilmentSaga, ParallelFulfilmentState>()
            .AddSaga<TestTerminalJoinSaga, TerminalJoinState>());

        using var provider = services.BuildServiceProvider();

        // Both definitions resolve to their own instance rather than one shadowing the other.
        Assert.NotNull(provider.GetRequiredService<TestParallelFulfilmentSaga>());
        Assert.NotNull(provider.GetRequiredService<TestTerminalJoinSaga>());
    }
}

/// <summary>Exists only to collide with <see cref="TestParallelFulfilmentSaga"/>'s state type.</summary>
public sealed class AnotherSagaSharingAState : Dsl.OrchestratedSagaDefinition<ParallelFulfilmentState>
{
    public AnotherSagaSharingAState()
    {
        var placed = InitialState("Placed");
        During(placed).When<ParallelOrderPlaced>().TransitionTo(placed);
    }
}
