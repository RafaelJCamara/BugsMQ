using BugsMQ.Abstractions.Transport;
using BugsMQ.Transport.Common;
using Microsoft.Extensions.DependencyInjection;

namespace BugsMQ.Transport.Brighter;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Paramore.Brighter-based transport, wrapped in the (currently empty)
    /// outbound/inbound middleware pipeline — mirrors <c>AddBugsMqRabbitMq</c>'s shape exactly. Register
    /// <see cref="IOutboundMessageMiddleware"/>/<see cref="IInboundMessageMiddleware"/> implementations
    /// before calling this to have them picked up.
    ///
    /// Deliberately does not use Brighter's own <c>services.AddBrighter(...).UseExternalBus(...)</c> DI
    /// story: that wires up CommandProcessor, an outbox-backed producer registry, and the Service
    /// Activator's dispatcher — all part of the higher-level stack this adapter must not depend on (see
    /// the class docs on <see cref="BrighterTransport"/>). Registering the same
    /// <see cref="MiddlewarePipelineTransport"/> every other adapter uses, directly around
    /// <see cref="BrighterTransport"/>, is the least-surprising way to expose this adapter through the
    /// same one-call shape as its siblings without dragging that stack in.
    /// </summary>
    public static IServiceCollection AddBugsMqBrighter(this IServiceCollection services, Action<BrighterOptions>? configure = null)
    {
        var options = new BrighterOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<BrighterTransport>();
        services.AddSingleton<IMessageTransport>(sp => new MiddlewarePipelineTransport(
            sp.GetRequiredService<BrighterTransport>(),
            sp.GetServices<IOutboundMessageMiddleware>(),
            sp.GetServices<IInboundMessageMiddleware>()));

        return services;
    }
}
