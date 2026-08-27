using System.Net;
using VSaga.Abstractions.Sagas;

namespace VSaga.Http.Tests;

/// <summary>
/// docs/mixed-sagas.md §4/§9: the same mapping table CallHttpMappingTests pins for the declarative
/// <c>.CallHttp</c>, mirrored for the imperative <c>ctx.CallHttpAsync</c> -- proving both DSL entry
/// points drive the identical shared executor identically.
/// </summary>
public sealed class CallHttpAsyncMappingTests
{
    [Fact]
    public async Task TwoHundredResponse_MapsToTheConfiguredOnSuccessMessage()
    {
        await using var harness = CallHttpAsyncTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"body":"charged"}"""));

        await harness.WhenAsync(new BeginHttpCallAsync("REQ-A1"));

        var state = await harness.AssertStateAsync(harness.Saga.Succeeded);
        Assert.Equal(SagaStatus.Completed, state.Status);
        Assert.Equal("charged", state.Result);
    }

    [Fact]
    public async Task ExplicitStatus_MapsToItsOwnConfiguredMessage()
    {
        await using var harness = CallHttpAsyncTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse((HttpStatusCode)402, """{"reason":"card_declined"}"""));

        await harness.WhenAsync(new BeginHttpCallAsync("REQ-A2"));

        var state = await harness.AssertStateAsync(harness.Saga.Declined);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal("card_declined", state.Result);
    }

    [Fact]
    public async Task FiveHundredResponse_MapsToTheConfiguredOnFailureMessage()
    {
        await using var harness = CallHttpAsyncTestHarness.Create(_ =>
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.InternalServerError, """{"reason":"gateway_down"}"""));

        await harness.WhenAsync(new BeginHttpCallAsync("REQ-A3"));

        var state = await harness.AssertStateAsync(harness.Saga.Failed);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Equal("gateway_down", state.Result);
    }

    [Fact]
    public async Task NetworkFailure_WithNoResponseAtAll_AlsoMapsToTheConfiguredOnFailureMessage()
    {
        await using var harness = CallHttpAsyncTestHarness.Create(_ => throw new HttpRequestException("connection refused"));

        await harness.WhenAsync(new BeginHttpCallAsync("REQ-A4"));

        var state = await harness.AssertStateAsync(harness.Saga.Failed);
        Assert.Equal(SagaStatus.Failed, state.Status);
        Assert.Null(state.Result); // deserialized from "{}" -- no body to hydrate a reason from
    }

    [Fact]
    public async Task Timeout_AlsoMapsToTheConfiguredOnFailureMessage()
    {
        await using var harness = CallHttpAsyncTestHarness.Create(_ => throw new TaskCanceledException("request timed out"));

        await harness.WhenAsync(new BeginHttpCallAsync("REQ-A5"));

        var state = await harness.AssertStateAsync(harness.Saga.Failed);
        Assert.Equal(SagaStatus.Failed, state.Status);
    }
}
