using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace BugsMQ.Transport.InMemory;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the in-process, no-broker transport used for local dev and BugsMQ.Testing.</summary>
    public static IServiceCollection AddBugsMqInMemoryTransport(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryMessageTransport>();
        services.AddSingleton<IMessageTransport>(sp => sp.GetRequiredService<InMemoryMessageTransport>());
        return services;
    }
}
