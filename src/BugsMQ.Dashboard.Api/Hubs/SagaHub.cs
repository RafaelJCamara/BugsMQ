using BugsMQ.Abstractions.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace BugsMQ.Dashboard.Api.Hubs;

public interface ISagaHubClient
{
    Task SagaUpdated(SagaSummary summary);

    Task TimelineEntryAdded(Guid correlationId, SagaLogEntry entry);
}

/// <summary>
/// Two subscription granularities: the list view joins <see cref="ListGroup"/> for any-saga-changed
/// pings, a detail view additionally joins <see cref="GroupForSaga"/> for that one saga's timeline.
/// </summary>
public sealed class SagaHub : Hub<ISagaHubClient>
{
    public const string ListGroup = "saga:list";

    public static string GroupForSaga(Guid correlationId) => $"saga:{correlationId}";

    public Task SubscribeToList() => Groups.AddToGroupAsync(Context.ConnectionId, ListGroup);

    public Task UnsubscribeFromList() => Groups.RemoveFromGroupAsync(Context.ConnectionId, ListGroup);

    public Task SubscribeToSaga(Guid correlationId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupForSaga(correlationId));

    public Task UnsubscribeFromSaga(Guid correlationId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForSaga(correlationId));
}
