using VSaga.Abstractions.Transport;
using VSaga.Transport.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Transport.RabbitMQ;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RabbitMQ.Client-based transport, wrapped in the (currently empty)
    /// outbound/inbound middleware pipeline. Register <see cref="IOutboundMessageMiddleware"/>/
    /// <see cref="IInboundMessageMiddleware"/> implementations before calling this to have them
    /// picked up.
    /// </summary>
    public static IServiceCollection AddVSagaRabbitMq(this IServiceCollection services, Action<RabbitMqOptions>? configure = null)
    {
        var options = new RabbitMqOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<RabbitMqConnectionManager>();
        services.TryAddSingleton<IRoutingKeyConvention, DefaultRoutingKeyConvention>();
        services.AddSingleton<RabbitMqTransport>();
        services.AddSingleton<IMessageTransport>(sp => new MiddlewarePipelineTransport(
            sp.GetRequiredService<RabbitMqTransport>(),
            sp.GetServices<IOutboundMessageMiddleware>(),
            sp.GetServices<IInboundMessageMiddleware>()));

        return services;
    }
}
