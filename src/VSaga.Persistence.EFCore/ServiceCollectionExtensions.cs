using VSaga.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Persistence.EFCore;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EF Core persistence provider: <see cref="VSagaDbContext"/> (Scoped — a fresh
    /// DbContext per message/timeout/retry, matching how VSaga.Core resolves per-unit-of-work
    /// services) plus EF-backed implementations of all four persistence contracts. Pass the provider
    /// hookup (UseNpgsql/UseSqlServer/UseSqlite/...) via <paramref name="configureDbContext"/> — this
    /// package itself only depends on Microsoft.EntityFrameworkCore, not any specific provider.
    /// </summary>
    public static IServiceCollection AddVSagaEfCore(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDbContext)
    {
        services.AddDbContext<VSagaDbContext>(configureDbContext);
        services.AddScoped(typeof(ISagaSnapshotStore<>), typeof(EfCoreSagaSnapshotStore<>));
        services.AddScoped<EfCoreSagaSummaryReader>();
        services.AddScoped<ISagaSummaryReader>(sp => sp.GetRequiredService<EfCoreSagaSummaryReader>());
        services.AddScoped<ISagaAdminStore>(sp => sp.GetRequiredService<EfCoreSagaSummaryReader>());
        services.AddScoped<ISagaEventLogStore, EfCoreSagaEventLogStore>();
        services.AddScoped<ISagaTimeoutStore, EfCoreSagaTimeoutStore>();
        services.AddScoped<IServiceTopologyStore, EfCoreServiceTopologyStore>();
        return services;
    }
}
