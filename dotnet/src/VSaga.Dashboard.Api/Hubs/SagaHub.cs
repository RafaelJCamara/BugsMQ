using VSaga.Abstractions.Persistence;
using Microsoft.AspNetCore.SignalR;

namespace VSaga.Dashboard.Api.Hubs;

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

    // correlationId is a string, not a Guid, deliberately: SignalR's default model binder rejects a
    // non-Guid-shaped argument by failing the whole hub invocation before this method body ever runs,
    // which surfaces client-side as "Failed to invoke 'SubscribeToSaga' due to an error on the
    // server" -- needless server-side noise for what's usually just a stale or hand-edited detail-page
    // URL. Parsing it ourselves lets that case join no group instead, matching how the REST endpoint
    // for the same malformed id already degrades (a clean 404, not a crash).
    public Task SubscribeToSaga(string sagaType, string correlationId) =>
        Guid.TryParse(correlationId, out var parsed)
            ? Groups.AddToGroupAsync(Context.ConnectionId, GroupForSaga(sagaType, parsed))
            : Task.CompletedTask;

    public Task UnsubscribeFromSaga(string sagaType, string correlationId) =>
        Guid.TryParse(correlationId, out var parsed)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForSaga(sagaType, parsed))
            : Task.CompletedTask;
}
