using BugsMQ.Abstractions.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace BugsMQ.Dashboard.Api.Hubs;

public interface ISagaHubClient
{
    Task SagaUpdated(SagaSummary summary);

    Task TimelineEntryAdded(string sagaType, Guid correlationId, SagaLogEntry entry);
}

/// <summary>
/// Two subscription granularities: the list view joins <see cref="ListGroup"/> for any-saga-changed
/// pings, a detail view additionally joins <see cref="GroupForSaga"/> for that one saga's timeline.
/// </summary>
public sealed class SagaHub : Hub<ISagaHubClient>
{
    public const string ListGroup = "saga:list";

    /// <summary>
    /// Keyed by the full saga instance identity, not the correlation id alone: two saga types may
    /// track the same correlation id, and a detail view subscribed to one of them must not receive
    /// the other's timeline entries.
    /// </summary>
    public static string GroupForSaga(string sagaType, Guid correlationId) => $"saga:{sagaType}:{correlationId}";

    public Task SubscribeToList() => Groups.AddToGroupAsync(Context.ConnectionId, ListGroup);

    public Task UnsubscribeFromList() => Groups.RemoveFromGroupAsync(Context.ConnectionId, ListGroup);

    public Task SubscribeToSaga(string sagaType, Guid correlationId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupForSaga(sagaType, correlationId));

    public Task UnsubscribeFromSaga(string sagaType, Guid correlationId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForSaga(sagaType, correlationId));
}
