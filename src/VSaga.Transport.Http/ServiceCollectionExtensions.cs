using VSaga.Abstractions.Transport;
using VSaga.Transport.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace VSaga.Transport.Http;

public static class ServiceCollectionExtensions
{
    private const string HttpClientName = "VSagaHttp";

    /// <summary>
    /// Registers the HTTP-based transport, wrapped in the (currently empty) outbound/inbound
    /// middleware pipeline -- same one-call shape as AddVSagaRabbitMq, and for the same reason
    /// (docs/http-based-sagas.md §4.5): AddVSagaTopologyRecording requires the last IMessageTransport
    /// registration to carry a factory, so this must be a factory registration too, never a bare
    /// instance/type registration.
    /// </summary>
    public static IServiceCollection AddVSagaHttp(this IServiceCollection services, Action<HttpTransportOptions>? configure = null)
    {
        var options = new HttpTransportOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddHttpClient(HttpClientName);
        services.TryAddSingleton<IHttpRouteTable, ConfigHttpRouteTable>();
        services.AddSingleton<HttpInboundDispatcher>();
        services.AddSingleton(sp => new HttpMessageTransport(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            options,
            sp.GetRequiredService<IHttpRouteTable>(),
            sp.GetRequiredService<HttpInboundDispatcher>(),
            sp.GetRequiredService<ILogger<HttpMessageTransport>>()));
        services.AddSingleton<IMessageTransport>(sp => new MiddlewarePipelineTransport(
            sp.GetRequiredService<HttpMessageTransport>(),
            sp.GetServices<IOutboundMessageMiddleware>(),
            sp.GetServices<IInboundMessageMiddleware>()));

        return services;
    }
}
