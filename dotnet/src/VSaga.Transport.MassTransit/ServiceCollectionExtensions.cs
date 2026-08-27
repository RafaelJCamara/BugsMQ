using VSaga.Abstractions.Transport;
using VSaga.Transport.Common;
using global::MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Transport.MassTransit;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a MassTransit-over-RabbitMQ transport, wrapped in the same outbound/inbound middleware
    /// pipeline <c>AddVSagaRabbitMq</c> uses. <paramref name="configure"/> only ever touches
    /// <see cref="MassTransitOptions"/> (connection string, shared exchange name) — MassTransit's own
    /// much larger <c>IBusRegistrationConfigurator</c>/<c>IRabbitMqBusFactoryConfigurator</c> surface is
    /// deliberately not exposed here, so this call matches <c>AddVSagaRabbitMq</c>'s shape exactly rather
    /// than leaking a second, adapter-specific configuration model into callers.
    /// </summary>
    public static IServiceCollection AddVSagaMassTransit(this IServiceCollection services, Action<MassTransitOptions>? configure = null)
    {
        var options = new MassTransitOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);

        services.AddMassTransit(x =>
        {
            x.UsingRabbitMq((_, cfg) =>
            {
                cfg.Host(new Uri(options.ConnectionString));

                // Every VSaga message shares this one MassTransit contract — see VSagaEnvelopeMessage's
                // doc comment. Forcing it onto one durable topic exchange, with the routing key read back
                // off the message itself, is what lets SubscribeAsync bind a queue to exactly the message
                // type names a given subscription declares (via IRabbitMqReceiveEndpointConfigurator.Bind
                // in MassTransitTransport), the same shape RabbitMqTransport gets from its own shared
                // topic exchange + per-type routing key.
                cfg.Message<VSagaEnvelopeMessage>(m => m.SetEntityName(options.ExchangeName));
                cfg.Publish<VSagaEnvelopeMessage>(p => p.ExchangeType = "topic");
                cfg.Send<VSagaEnvelopeMessage>(s => s.UseRoutingKeyFormatter(context => context.Message.MessageTypeName));
            });
        });

        services.AddSingleton<MassTransitTransport>();
        services.AddSingleton<IMessageTransport>(sp => new MiddlewarePipelineTransport(
            sp.GetRequiredService<MassTransitTransport>(),
            sp.GetServices<IOutboundMessageMiddleware>(),
            sp.GetServices<IInboundMessageMiddleware>()));

        return services;
    }
}
