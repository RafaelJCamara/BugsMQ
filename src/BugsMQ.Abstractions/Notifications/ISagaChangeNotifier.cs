using BugsMQ.Abstractions.Persistence;

namespace BugsMQ.Abstractions.Notifications;

/// <summary>
/// Fired by the orchestrator on every persisted saga change. Core's default implementation is a
/// no-op; BugsMQ.Dashboard.Api registers a SignalR-backed implementation. Keeps Core free of any
/// SignalR/web dependency.
/// </summary>
public interface ISagaChangeNotifier
{
    Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default);

    Task TimelineEntryAddedAsync(Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default);
}

public sealed class NullSagaChangeNotifier : ISagaChangeNotifier
{
    public static readonly NullSagaChangeNotifier Instance = new();

    public Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task TimelineEntryAddedAsync(Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
