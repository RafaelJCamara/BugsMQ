using VSaga.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Http.Tests;

/// <summary>Builds a SagaTestHarness for CallHttpTestSaga with its outbound HttpClient's primary handler swapped for a stub -- see StubHttpMessageHandler.</summary>
internal static class CallHttpTestHarness
{
    public static SagaTestHarness<CallHttpTestSaga, CallHttpTestState> Create(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(services =>
        {
            services.AddVSagaHttpCalls();
            services.AddHttpClient(ServiceCollectionExtensions.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(respond));
        });
}
