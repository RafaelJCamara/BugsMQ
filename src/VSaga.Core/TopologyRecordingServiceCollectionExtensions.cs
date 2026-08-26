using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace VSaga.Core;

public static class TopologyRecordingServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the already-registered IMessageTransport in a TopologyRecordingTransport so every
    /// SubscribeAsync call (orchestrator and participants alike) records who consumes what — the only
    /// way the saga map can name a destination that never actually replied. Call this after
    /// AddVSagaRabbitMq/AddVSagaInMemoryTransport (so there's a transport to wrap) and after
    /// AddVSagaEfCore/AddVSagaInMemoryPersistence if you want the recording to persist (otherwise it
    /// safely no-ops against <see cref="NullServiceTopologyStore"/>).
    /// </summary>
    public static IServiceCollection AddVSagaTopologyRecording(this IServiceCollection services)
    {
        services.TryAddSingleton<IServiceTopologyStore>(NullServiceTopologyStore.Instance);

        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IMessageTransport));
        if (existing?.ImplementationFactory is null)
        {
            throw new InvalidOperationException(
                "AddVSagaTopologyRecording requires an IMessageTransport factory registration already present " +
                "— call AddVSagaRabbitMq or AddVSagaInMemoryTransport first.");
        }

        services.Remove(existing);

        services.AddSingleton<IMessageTransport>(sp =>
        {
            var inner = (IMessageTransport)existing.ImplementationFactory(sp);
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var logger = sp.GetRequiredService<ILogger<TopologyRecordingTransport>>();

            return new TopologyRecordingTransport(inner, (subscription, ct) => RecordSubscriptionAsync(subscription, scopeFactory, logger, ct));
        });

        return services;
    }

    private static async Task RecordSubscriptionAsync(TransportSubscription subscription, IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IServiceTopologyStore>();
            var now = DateTimeOffset.UtcNow;

            foreach (var messageType in subscription.MessageTypes)
                await store.RecordAsync(subscription.ConsumerName, messageType.Name, subscription.QueueNameHint, now, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort visibility for the map, not load-bearing for message processing — a cold
            // database here must never block a subscriber from starting, mirroring the non-fatal
            // MigrateAsync pattern at Dashboard.Api/Program.cs.
            logger.LogWarning(ex, "Failed to record topology for consumer {ConsumerName}", subscription.ConsumerName);
        }
    }
}
