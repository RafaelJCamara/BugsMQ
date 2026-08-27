using System.Net;
using System.Text;
using VSaga.Abstractions.Sagas;

namespace VSaga.Http.Tests;

/// <summary>
/// docs/mixed-sagas.md §4/§9: pins that <c>ctx.CallHttpAsync</c>'s own <c>.WithRetry(...)</c> re-invokes
/// (re-serializes) its <c>.Body(...)</c> value once per attempt, exactly as the pre-refactor <c>.CallHttp</c>
/// re-invoked its message-aware body factory per attempt. This is what the manual mutation test in this
/// commit's message targets: hoisting the shared executor's <c>body()</c> call out of the retry loop
/// would make every attempt send the exact same bytes on the wire, which this test would catch (every
/// captured body would read <c>{"Attempt":1}</c>) but nothing else would.
/// </summary>
public sealed class CallHttpAsyncRetryTests
{
    [Fact]
    public async Task RetriedCall_SendsAFreshlySerializedBodyOnEveryAttempt()
    {
        var sentBodies = new List<string>();

        await using var harness = CallHttpAsyncTestHarness.CreateForRetry(request =>
        {
            var bytes = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? [];
            sentBodies.Add(Encoding.UTF8.GetString(bytes));

            return sentBodies.Count < 3
                ? throw new HttpRequestException("simulated transient network failure")
                : StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"body":"ok"}""");
        });

        await harness.WhenAsync(new BeginRetryCall("REQ-RETRY-1"));

        var state = await harness.AssertStateAsync(harness.Saga.Succeeded);
        Assert.Equal(SagaStatus.Completed, state.Status);

        // Three attempts, and -- the actual pin -- three DIFFERENT bodies: {"Attempt":1}, {"Attempt":2},
        // {"Attempt":3}. A hoisted body() call would serialize once and resend {"Attempt":1} three times.
        Assert.Equal(3, sentBodies.Count);
        Assert.Equal(["""{"Attempt":1}""", """{"Attempt":2}""", """{"Attempt":3}"""], sentBodies);
    }
}
