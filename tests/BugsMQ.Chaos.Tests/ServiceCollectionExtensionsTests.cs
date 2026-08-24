using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BugsMQ.Chaos.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    [Fact]
    public void AllFaultsDisabledByDefault_RegistersNoMiddleware()
    {
        var services = NewServices();
        services.AddBugsMqChaos();
        using var provider = services.BuildServiceProvider();

        Assert.Empty(provider.GetServices<IOutboundMessageMiddleware>());
        Assert.Empty(provider.GetServices<IInboundMessageMiddleware>());
    }

    [Fact]
    public void SingleFaultEnabled_RegistersOnlyThatFaultsMiddleware()
    {
        var services = NewServices();
        services.AddBugsMqChaos(o => o.Delay.Enabled = true);
        using var provider = services.BuildServiceProvider();

        var outbound = provider.GetServices<IOutboundMessageMiddleware>().ToList();
        var inbound = provider.GetServices<IInboundMessageMiddleware>().ToList();

        Assert.Single(outbound);
        Assert.IsType<DelayOutboundMiddleware>(outbound[0]);
        Assert.Single(inbound);
        Assert.IsType<DelayInboundMiddleware>(inbound[0]);
    }

    [Fact]
    public void EnabledFault_ApplyToOutboundFalse_RegistersOnlyInboundSide()
    {
        var services = NewServices();
        services.AddBugsMqChaos(o =>
        {
            o.Drop.Enabled = true;
            o.Drop.ApplyToOutbound = false;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Empty(provider.GetServices<IOutboundMessageMiddleware>());
        var inbound = Assert.Single(provider.GetServices<IInboundMessageMiddleware>());
        Assert.IsType<DropInboundMiddleware>(inbound);
    }

    [Fact]
    public void AllFaultsEnabled_RegistersAllSixMiddlewareInstances()
    {
        var services = NewServices();
        services.AddBugsMqChaos(o =>
        {
            o.Delay.Enabled = true;
            o.Drop.Enabled = true;
            o.Duplicate.Enabled = true;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Equal(3, provider.GetServices<IOutboundMessageMiddleware>().Count());
        Assert.Equal(3, provider.GetServices<IInboundMessageMiddleware>().Count());
    }

    [Fact]
    public void Configure_BindsOptionsRegisteredAsSingleton()
    {
        var services = NewServices();
        services.AddBugsMqChaos(o => o.Drop.Probability = 0.42);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(0.42, provider.GetRequiredService<ChaosOptions>().Drop.Probability);
    }
}
