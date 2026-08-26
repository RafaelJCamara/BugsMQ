using VSaga.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Chaos;

/// <summary>
/// Registers fault-injection middleware into the existing <see cref="IOutboundMessageMiddleware"/>/
/// <see cref="IInboundMessageMiddleware"/> seam that <c>MiddlewarePipelineTransport</c> already wraps
/// every transport in — opt-in only, matching <c>LoggingMessageMiddleware</c>'s convention of never
/// being registered unless a caller explicitly asks for it. Call this anywhere before the host is
/// built; registration order relative to <c>AddVSagaRabbitMq</c> doesn't matter, since the transport
/// only resolves <c>IEnumerable&lt;IOutboundMessageMiddleware&gt;</c>/<c>IInboundMessageMiddleware</c>
/// lazily when <see cref="IMessageTransport"/> is first resolved, by which point every <c>AddXyz</c>
/// call in <c>Program.cs</c> has already run.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Only fault types with <c>Enabled = true</c> (and, per direction, <c>ApplyToOutbound</c>/
    /// <c>ApplyToInbound</c>) actually get registered — a disabled fault costs nothing at runtime, not
    /// even a probability check, because its middleware never joins the pipeline in the first place.
    /// </summary>
    public static IServiceCollection AddVSagaChaos(this IServiceCollection services, Action<ChaosOptions>? configure = null)
    {
        var options = new ChaosOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddSingleton<IChaosRandomSource, ThreadRandomChaosSource>();
        services.TryAddSingleton(TimeProvider.System);

        if (options.Delay.Enabled)
        {
            if (options.Delay.ApplyToOutbound)
                services.AddSingleton<IOutboundMessageMiddleware, DelayOutboundMiddleware>();

            if (options.Delay.ApplyToInbound)
                services.AddSingleton<IInboundMessageMiddleware, DelayInboundMiddleware>();
        }

        if (options.Drop.Enabled)
        {
            if (options.Drop.ApplyToOutbound)
                services.AddSingleton<IOutboundMessageMiddleware, DropOutboundMiddleware>();

            if (options.Drop.ApplyToInbound)
                services.AddSingleton<IInboundMessageMiddleware, DropInboundMiddleware>();
        }

        if (options.Duplicate.Enabled)
        {
            if (options.Duplicate.ApplyToOutbound)
                services.AddSingleton<IOutboundMessageMiddleware, DuplicateOutboundMiddleware>();

            if (options.Duplicate.ApplyToInbound)
                services.AddSingleton<IInboundMessageMiddleware, DuplicateInboundMiddleware>();
        }

        return services;
    }
}
