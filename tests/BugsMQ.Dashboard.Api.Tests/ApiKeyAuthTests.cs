using System.Net;

namespace BugsMQ.Dashboard.Api.Tests;

public sealed class ApiKeyAuthTests : IAsyncDisposable
{
    private readonly DashboardApiFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task ListSagas_WithoutApiKey_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListSagas_WithWrongApiKey_Returns401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-right-key");

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListSagas_WithCorrectApiKey_Returns200()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", DashboardApiFactory.TestApiKey);

        var response = await client.GetAsync("/api/sagas");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithoutApiKey_StillReachable()
    {
        // /health is deliberately left open for infra probes even though every other endpoint requires a key.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
