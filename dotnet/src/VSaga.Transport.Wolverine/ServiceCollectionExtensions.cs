using VSaga.Abstractions.Transport;
using VSaga.Transport.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace VSaga.Transport.Wolverine;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Wolverine itself (<c>services.AddWolverine</c>) wired to RabbitMQ purely as a low-level
    /// transport — see <see cref="WolverineTransport"/>'s doc comment for why its own saga support,
    /// transactional inbox/outbox, and message-type-based handler discovery are deliberately never
    /// exercised for VSaga business messages — then wraps the resulting <see cref="IMessageTransport"/>
    /// in the (currently empty) outbound/inbound middleware pipeline. Register
    /// <see cref="IOutboundMessageMiddleware"/>/<see cref="IInboundMessageMiddleware"/> implementations
    /// before calling this to have them picked up.
    /// </summary>
    public static IServiceCollection AddVSagaWolverine(this IServiceCollection services, Action<WolverineTransportOptions>? configure = null)
    {
        var options = new WolverineTransportOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddSingleton<RawDispatchRegistry>();

        services.AddWolverine(opts =>
        {
            opts.ServiceName = "VSaga.Transport.Wolverine";

            // RawEnvelopeHandler lives in this assembly, not the host application's entry assembly, so it
            // needs an explicit include for Wolverine's conventional handler discovery to find it.
            opts.Discovery.IncludeAssembly(typeof(RawEnvelopeHandler).Assembly);

            opts.UseRabbitMq(options.ConnectionString)
                .DeclareExchange(options.ExchangeName, exchange => exchange.ExchangeType = ExchangeType.Topic);

            // Core (SagaOrchestrator.HandleInfrastructureFailureAsync) already owns bounded,
            // application-level redelivery via PublishRawAsync plus an incremented
            // x-vsaga-delivery-attempt header, and never relies on broker-native requeue. Wolverine's own
            // default retry-then-error-queue policy would duplicate/fight with that, so every exception
            // here goes straight to Wolverine's error queue on the first failure — matching
            // NackAsync(requeue: false)'s "settle this as rejected" contract, not RabbitMQ-native requeue.
            opts.OnException<Exception>().MoveToErrorQueue();
        });

        services.AddSingleton<WolverineTransport>();
        services.AddSingleton<IMessageTransport>(sp => new MiddlewarePipelineTransport(
            sp.GetRequiredService<WolverineTransport>(),
            sp.GetServices<IOutboundMessageMiddleware>(),
            sp.GetServices<IInboundMessageMiddleware>()));

        return services;
    }
}
