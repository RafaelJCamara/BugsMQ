using VSaga.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Http.Tests;

/// <summary>Builds a SagaTestHarness with its outbound HttpClient's primary handler swapped for a stub -- see StubHttpMessageHandler. One factory method per ctx.CallHttpAsync fixture saga, mirroring CallHttpTestHarness.</summary>
internal static class CallHttpAsyncTestHarness
{
    public static SagaTestHarness<CallHttpAsyncTestSaga, CallHttpAsyncTestState> Create(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(services => Configure(services, respond));

    public static SagaTestHarness<CallHttpAsyncRetryTestSaga, CallHttpAsyncRetryTestState> CreateForRetry(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(services => Configure(services, respond));

    public static SagaTestHarness<MixedCompensationTestSaga, MixedCompensationTestState> CreateForCompensation(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(services => Configure(services, respond));

    private static void Configure(IServiceCollection services, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        services.AddVSagaHttpCalls();
        services.AddHttpClient(ServiceCollectionExtensions.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(respond));
    }
}
