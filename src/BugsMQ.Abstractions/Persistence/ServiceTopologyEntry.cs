namespace BugsMQ.Abstractions.Persistence;

/// <summary>One observed (service, message type) consumer binding, learned from a real SubscribeAsync call.</summary>
public sealed record ServiceTopologyEntry(string ServiceName, string MessageType, string QueueName, DateTimeOffset LastSeenAtUtc);

/// <summary>
/// Records which service consumes which message type, populated from real SubscribeAsync calls (see
/// TopologyRecordingTransport). Lets the saga map name a destination that never actually replied — e.g.
/// a hung participant — instead of rendering an unresolved placeholder.
/// </summary>
public interface IServiceTopologyStore
{
    /// <summary>Upserts the (ServiceName, MessageType) binding, refreshing QueueName/LastSeenAtUtc if it was already known.</summary>
    Task RecordAsync(string serviceName, string messageType, string queueName, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServiceTopologyEntry>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Safe default for hosts that never call AddBugsMqTopologyRecording() — every destination simply stays unresolved.</summary>
public sealed class NullServiceTopologyStore : IServiceTopologyStore
{
    public static readonly NullServiceTopologyStore Instance = new();

    private NullServiceTopologyStore()
    {
    }

    public Task RecordAsync(string serviceName, string messageType, string queueName, DateTimeOffset seenAtUtc, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ServiceTopologyEntry>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ServiceTopologyEntry>>([]);
}
