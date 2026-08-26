using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Transport;
using VSaga.Persistence.EFCore;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;
using VSaga.Transport.RabbitMQ;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Boots the real Program.cs but swaps EF Core/Postgres and RabbitMQ for the in-memory persistence
/// and transport providers, so the endpoint tests exercise the actual HTTP pipeline, routing, and
/// SagaEndpoints logic (including the retry redrive/fallback logic) without needing Docker or a live
/// database. <see cref="HealthEndpointTests"/> separately verifies the real (untouched) Program.cs
/// composition root resolves correctly.
/// </summary>
public sealed class DashboardApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Configured below so authenticated endpoint tests have a key to send; auth fails closed otherwise.</summary>
    public const string TestApiKey = "test-api-key";

    public InMemoryMessageTransport Transport => Services.GetRequiredService<InMemoryMessageTransport>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Dashboard:ApiKey"] = TestApiKey,
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<VSagaDbContext>>();
            services.RemoveAll<VSagaDbContext>();
            services.RemoveAll(typeof(ISagaSnapshotStore<>));
            services.RemoveAll<ISagaSummaryReader>();
            services.RemoveAll<ISagaEventLogStore>();
            services.RemoveAll<ISagaTimeoutStore>();
            services.RemoveAll<ISagaAdminStore>();
            services.RemoveAll<IServiceTopologyStore>();
            services.RemoveAll<EfCoreSagaSummaryReader>();

            services.RemoveAll<RabbitMqOptions>();
            services.RemoveAll<RabbitMqConnectionManager>();
            services.RemoveAll<RabbitMqTransport>();
            services.RemoveAll<IRoutingKeyConvention>();
            services.RemoveAll<IMessageTransport>();

            services.AddVSagaInMemoryPersistence();
            services.AddVSagaInMemoryTransport();
        });
    }
}
