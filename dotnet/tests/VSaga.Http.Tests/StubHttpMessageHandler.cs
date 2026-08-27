namespace VSaga.Http.Tests;

/// <summary>
/// Swapped in as the named client's primary handler (see CallHttpTestHarness) so every test drives
/// .CallHttp against a canned, in-process response instead of a real socket -- including a network-level
/// failure, by having <paramref name="respond"/> throw instead of returning.
/// </summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));

    public static HttpResponseMessage JsonResponse(System.Net.HttpStatusCode statusCode, string json) =>
        new(statusCode) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
