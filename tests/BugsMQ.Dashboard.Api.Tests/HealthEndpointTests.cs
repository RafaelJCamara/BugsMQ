using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace BugsMQ.Dashboard.Api.Tests;

/// <summary>
/// Verifies the composition root actually resolves — every AddBugsMqXxx registration, SignalR, CORS,
/// and the endpoint mappings — without needing a live Postgres/RabbitMQ (neither connects eagerly at
/// startup), and that /health's Postgres/RabbitMQ checks actually detect an unreachable dependency.
/// Points both connection strings at port 1 (nothing listens there, so the OS returns
/// connection-refused immediately — no risk of the test hanging on a real timeout) instead of relying
/// on "no local infra happens to be running" being true. This is the one check in this suite that
/// doesn't need Docker; DB/broker-backed endpoints need a real docker-compose stack, covered by the
/// end-to-end checkpoint instead.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:BugsMQ"] = "Host=localhost;Port=1;Database=bugsmq;Username=postgres;Password=postgres;Timeout=1",
                ["RabbitMq:ConnectionString"] = "amqp://guest:guest@localhost:1/",
            })));

    [Fact]
    public async Task Health_WithUnreachableDependencies_Returns503AndReportsBothChecksUnhealthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"unhealthy\"", body, StringComparison.Ordinal);
        Assert.Contains("\"postgres\"", body, StringComparison.Ordinal);
        Assert.Contains("\"rabbitmq\"", body, StringComparison.Ordinal);
    }
}
