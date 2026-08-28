using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Http;

/// <summary>Tunables for the <see cref="HttpClient"/> every <c>.CallHttp(...)</c> call shares, registered by <see cref="ServiceCollectionExtensions.AddVSagaHttpCalls"/>.</summary>
public sealed class HttpCallOptions
{
    /// <summary>Per-request timeout, including the remote side's own processing time.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Named distinctly from <c>VSaga.Transport.Http</c>'s own <c>AddVSagaHttp</c> -- the two packages
    /// are unrelated (docs/design/http-based-sagas.md §1) and a host can easily need both (an inbound HTTP
    /// transport plus outbound <c>.CallHttp</c> calls), so sharing a name would force a using-directive
    /// collision on any caller that wants both.
    /// </summary>
    internal const string ClientName = "VSaga.Http.CallHttp";

    /// <summary>Registers the <see cref="HttpClient"/> every <c>.CallHttp(...)</c> call resolves via <c>ISagaContext.Services</c>. Required once per host before any saga using <c>.CallHttp</c> runs.</summary>
    public static IServiceCollection AddVSagaHttpCalls(this IServiceCollection services, Action<HttpCallOptions>? configure = null)
    {
        var options = new HttpCallOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddHttpClient(ClientName, (provider, client) => client.Timeout = provider.GetRequiredService<HttpCallOptions>().Timeout);

        return services;
    }
}
