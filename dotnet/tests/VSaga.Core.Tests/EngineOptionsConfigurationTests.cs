using VSaga.Core.Runtime;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VSaga.Core.Tests;

/// <summary>
/// <see cref="SagaOrchestratorOptions"/>/<see cref="SagaOutboxOptions"/> had no documented, discoverable
/// configuration path: <c>AddVSagaEngine</c>'s <c>TryAddSingleton</c> only leaves room for a caller who
/// already knows to pre-register their own instance before calling it -- an idiom only this test suite
/// demonstrated. <see cref="SagaEngineBuilder.ConfigureOrchestrator"/>/<c>ConfigureOutbox</c> close that
/// gap with a fluent alternative inside the same <c>AddVSagaEngine(...)</c> delegate.
/// </summary>
public sealed class EngineOptionsConfigurationTests
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
    public void ConfigureOrchestratorOverridesTheDefaultMaxDeliveryAttempts()
    {
        var services = NewServices();

        services.AddVSagaEngine(o => o.ConfigureOrchestrator(opt => opt.MaxDeliveryAttempts = 9));

        using var provider = services.BuildServiceProvider();
        Assert.Equal(9, provider.GetRequiredService<SagaOrchestratorOptions>().MaxDeliveryAttempts);
    }

    [Fact]
    public void ConfigureOutboxOverridesTheDefaultModeAndPollInterval()
    {
        var services = NewServices();

        services.AddVSagaEngine(o => o.ConfigureOutbox(opt =>
        {
            opt.Mode = SagaOutboxMode.All;
            opt.PollInterval = TimeSpan.FromSeconds(1);
        }));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<SagaOutboxOptions>();
        Assert.Equal(SagaOutboxMode.All, options.Mode);
        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
    }

    [Fact]
    public void NeitherConfigureCallStillResolvesLibraryDefaults()
    {
        var services = NewServices();

        services.AddVSagaEngine(_ => { });

        using var provider = services.BuildServiceProvider();
        Assert.Equal(5, provider.GetRequiredService<SagaOrchestratorOptions>().MaxDeliveryAttempts);
        Assert.Equal(SagaOutboxMode.Deferred, provider.GetRequiredService<SagaOutboxOptions>().Mode);
    }
}
