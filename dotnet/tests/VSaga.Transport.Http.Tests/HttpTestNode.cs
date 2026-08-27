using System.Collections.Concurrent;
using VSaga.Transport.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VSaga.Transport.Http.Tests;

/// <summary>
/// Routes an outbound request to whichever <see cref="HttpTestNode"/> is registered for its target
/// host, via TestServer's in-memory handler rather than a real socket -- what lets two (or more) HTTP
/// transport instances talk to each other inside one test process with no network at all.
/// </summary>
internal sealed class NodeRegistry
{
    private readonly ConcurrentDictionary<string, TestServer> _servers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string host, TestServer server) => _servers[host] = server;

    public HttpMessageHandler CreateRoutingHandler() => new RoutingHandler(this);

    private sealed class RoutingHandler(NodeRegistry registry) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host
                ?? throw new InvalidOperationException("Request has no target host.");

            if (!registry._servers.TryGetValue(host, out var server))
                throw new InvalidOperationException($"No test node is registered for host '{host}'.");

            using var invoker = new HttpMessageInvoker(server.CreateHandler());
            return await invoker.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// One vSaga-aware HTTP "service" for tests: a real ASP.NET Core pipeline with MapVSagaHttp() mapped,
/// hosted entirely in-memory via TestServer. <paramref name="host"/> is the synthetic hostname other
/// nodes address it by in their own HttpTransportOptions.Endpoints (e.g. "http://{host}").
/// </summary>
internal sealed class HttpTestNode : IAsyncDisposable
{
    private const string HttpClientName = "VSagaHttp";

    private readonly IHost _host;

    private HttpTestNode(IHost host) => _host = host;

    public static async Task<HttpTestNode> StartAsync(string host, NodeRegistry registry, Action<HttpTransportOptions> configureOptions)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddVSagaHttp(configureOptions);

                    // Registered after AddVSagaHttp's own AddHttpClient(HttpClientName) call, so this
                    // configuration -- routing every outbound call through the in-memory registry
                    // instead of a real socket -- is the one that wins.
                    services.AddHttpClient(HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() => registry.CreateRoutingHandler());
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapVSagaHttp());
                });
            });

        var builtHost = await hostBuilder.StartAsync();
        registry.Register(host, builtHost.GetTestServer());
        return new HttpTestNode(builtHost);
    }

    public T GetRequiredService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
