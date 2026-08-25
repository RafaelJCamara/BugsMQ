using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Dashboard.Api.Hubs;

namespace BugsMQ.Dashboard.Api.Tests;

/// <summary>
/// The in-process notifier: what the orchestrator's change callbacks turn into on the wire. Fires only
/// when a saga engine runs in the dashboard's own process; the cross-process case is
/// <see cref="SagaChangePollingServiceTests"/>.
/// </summary>
public sealed class SignalRSagaChangeNotifierTests
{
    private static SagaSummary NewSummary(string sagaType, Guid correlationId, SagaKind kind = SagaKind.Orchestrated) =>
        new(correlationId, sagaType, kind, "AwaitingPayment", SagaStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 2,
            ParentSagaType: null, ParentCorrelationId: null);

    [Fact]
    public async Task SagaUpdated_GoesToBothTheListGroupAndTheInstanceGroup()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();

        await notifier.SagaUpdatedAsync(NewSummary("OrderSaga", correlationId));

        var groups = context.Recorder.SagaUpdates.Select(c => c.Group).ToList();

        // The list view and an open detail page are separate subscriptions; a change has to reach both.
        Assert.Contains(SagaHub.ListGroup, groups, StringComparer.Ordinal);
        Assert.Contains(SagaHub.GroupForSaga("OrderSaga", correlationId), groups, StringComparer.Ordinal);
        Assert.Equal(2, groups.Count);
    }

    /// <summary>
    /// The instance group is derived from the summary's own SagaType, not from correlation id alone —
    /// otherwise an update for one saga would be delivered to a detail page watching a different saga
    /// type that happens to share the id.
    /// </summary>
    [Fact]
    public async Task SagaUpdated_TargetsTheInstanceGroupOfItsOwnSagaType()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();

        await notifier.SagaUpdatedAsync(NewSummary("PostShipmentChoreography", correlationId, SagaKind.Choreographed));

        var instanceGroups = context.Recorder.SagaUpdates
            .Select(c => c.Group)
            .Where(g => !string.Equals(g, SagaHub.ListGroup, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(SagaHub.GroupForSaga("PostShipmentChoreography", correlationId), Assert.Single(instanceGroups));
        Assert.DoesNotContain(SagaHub.GroupForSaga("OrderSaga", correlationId), instanceGroups, StringComparer.Ordinal);
    }

    [Fact]
    public async Task TimelineEntryAdded_GoesOnlyToTheInstanceGroupAndCarriesTheSagaType()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();
        var entry = SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived, messageType: "PaymentCharged");

        await notifier.TimelineEntryAddedAsync("OrderSaga", correlationId, entry);

        var call = Assert.Single(context.Recorder.TimelineEntries);
        Assert.Equal(SagaHub.GroupForSaga("OrderSaga", correlationId), call.Group);

        // The saga type travels as a payload argument too: the client filters on it before appending,
        // so dropping it here would let a sibling saga's entries render on the wrong detail page.
        Assert.Equal("OrderSaga", call.SagaType);
        Assert.Equal(correlationId, call.CorrelationId);
        Assert.Same(entry, call.Entry);

        // Timeline entries are per-instance detail, deliberately not broadcast to the list view.
        Assert.Empty(context.Recorder.SagaUpdates);
        Assert.DoesNotContain(context.Recorder.TimelineEntries, c => string.Equals(c.Group, SagaHub.ListGroup, StringComparison.Ordinal));
    }

    /// <summary>
    /// Two saga types sharing a correlation id must land in two different groups — the property the whole
    /// composite-key change exists to preserve, asserted here at the notification layer.
    /// </summary>
    [Fact]
    public async Task TwoSagaTypesSharingACorrelationIdNotifySeparateGroups()
    {
        var context = new RecordingHubContext();
        var notifier = new SignalRSagaChangeNotifier(context);
        var correlationId = Guid.NewGuid();

        await notifier.TimelineEntryAddedAsync("OrderSaga", correlationId,
            SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.MessageReceived));
        await notifier.TimelineEntryAddedAsync("PostShipmentChoreography", correlationId,
            SagaLogEntry.Create(correlationId, "PostShipmentChoreography", SagaEntryType.MessageReceived));

        var groups = context.Recorder.TimelineEntries.Select(c => c.Group).ToList();

        Assert.Equal(2, groups.Distinct(StringComparer.Ordinal).Count());
    }
}
