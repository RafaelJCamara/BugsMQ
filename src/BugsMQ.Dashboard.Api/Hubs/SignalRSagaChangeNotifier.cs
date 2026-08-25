using BugsMQ.Abstractions.Notifications;
using BugsMQ.Abstractions.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace BugsMQ.Dashboard.Api.Hubs;

/// <summary>The only place SignalR meets the saga engine — Core depends on ISagaChangeNotifier, never on this.</summary>
public sealed class SignalRSagaChangeNotifier(IHubContext<SagaHub, ISagaHubClient> hub) : ISagaChangeNotifier
{
    public async Task SagaUpdatedAsync(SagaSummary summary, CancellationToken cancellationToken = default)
    {
        await hub.Clients.Group(SagaHub.ListGroup).SagaUpdated(summary);
        await hub.Clients.Group(SagaHub.GroupForSaga(summary.SagaType, summary.CorrelationId)).SagaUpdated(summary);
    }

    public Task TimelineEntryAddedAsync(string sagaType, Guid correlationId, SagaLogEntry entry, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(SagaHub.GroupForSaga(sagaType, correlationId)).TimelineEntryAdded(sagaType, correlationId, entry);
}
