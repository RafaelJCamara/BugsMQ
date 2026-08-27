using VSaga.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Transport.InMemory;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the in-process, no-broker transport used for local dev and VSaga.Testing.</summary>
    public static IServiceCollection AddVSagaInMemoryTransport(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryMessageTransport>();
        services.AddSingleton<IMessageTransport>(sp => sp.GetRequiredService<InMemoryMessageTransport>());
        return services;
    }
}
