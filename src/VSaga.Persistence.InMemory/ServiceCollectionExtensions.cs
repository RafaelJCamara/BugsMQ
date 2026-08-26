using VSaga.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Persistence.InMemory;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory persistence provider: one shared <see cref="InMemorySagaStore"/> singleton
    /// backing <see cref="ISagaSnapshotStore{TState}"/> (open generic, one instance per saga TState),
    /// <see cref="ISagaSummaryReader"/>, <see cref="ISagaEventLogStore"/>, and <see cref="ISagaTimeoutStore"/>.
    /// Intended for local development and as the foundation of VSaga.Testing — not for production use.
    /// </summary>
    public static IServiceCollection AddVSagaInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<InMemorySagaStore>();
        services.AddSingleton<ISagaSummaryReader>(sp => sp.GetRequiredService<InMemorySagaStore>());
        services.AddSingleton<ISagaEventLogStore>(sp => sp.GetRequiredService<InMemorySagaStore>());
        services.AddSingleton<ISagaTimeoutStore>(sp => sp.GetRequiredService<InMemorySagaStore>());
        services.AddSingleton<ISagaAdminStore>(sp => sp.GetRequiredService<InMemorySagaStore>());
        services.AddSingleton<IServiceTopologyStore>(sp => sp.GetRequiredService<InMemorySagaStore>());
        services.AddSingleton(typeof(ISagaSnapshotStore<>), typeof(InMemorySagaSnapshotStore<>));
        return services;
    }
}
