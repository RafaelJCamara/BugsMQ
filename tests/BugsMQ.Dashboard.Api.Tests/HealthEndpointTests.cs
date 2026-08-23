using Microsoft.AspNetCore.Mvc.Testing;

namespace BugsMQ.Dashboard.Api.Tests;

/// <summary>
/// Verifies the composition root actually resolves — every AddBugsMqXxx registration, SignalR, CORS,
/// and the endpoint mappings — without needing a live Postgres/RabbitMQ (neither connects eagerly at
/// startup). This is the one check in this suite that doesn't need Docker; DB/broker-backed endpoints
/// need a real docker-compose stack, covered by the end-to-end checkpoint instead.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", body);
    }
}
