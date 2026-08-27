using VSaga.Abstractions.Persistence;
using VSaga.Dashboard.Api.Hubs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace VSaga.Dashboard.Api.Tests;

/// <summary>
/// Hand-written recording doubles for the SignalR surface, matching this repo's existing convention of
/// purpose-built fakes over a mocking library (see <c>FlakyEventLogStore</c> /
/// <c>RaceInjectingSnapshotStore</c> in VSaga.Core.Tests). Only the members the hub and notifier
/// actually use are implemented; the rest throw, so a future code path that starts addressing clients
/// some other way fails loudly here instead of being silently unasserted.
/// </summary>
internal sealed record SagaUpdatedCall(string Group, SagaSummary Summary);

internal sealed record TimelineEntryCall(string Group, string SagaType, Guid CorrelationId, SagaLogEntry Entry);

internal sealed class RecordingHubClient(string group, RecordingHubClients parent) : ISagaHubClient
{
    public Task SagaUpdated(SagaSummary summary)
    {
        parent.SagaUpdates.Add(new SagaUpdatedCall(group, summary));
        return Task.CompletedTask;
    }

    public Task TimelineEntryAdded(string sagaType, Guid correlationId, SagaLogEntry entry)
    {
        parent.TimelineEntries.Add(new TimelineEntryCall(group, sagaType, correlationId, entry));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingHubClients : IHubClients<ISagaHubClient>
{
    public List<SagaUpdatedCall> SagaUpdates { get; } = [];

    public List<TimelineEntryCall> TimelineEntries { get; } = [];

    public ISagaHubClient Group(string groupName) => new RecordingHubClient(groupName, this);

    public ISagaHubClient All => throw new NotSupportedException("Production code addresses groups only.");

    public ISagaHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

    public ISagaHubClient Client(string connectionId) => throw new NotSupportedException();

    public ISagaHubClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

    public ISagaHubClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

    public ISagaHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

    public ISagaHubClient User(string userId) => throw new NotSupportedException();

    public ISagaHubClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
}

internal sealed class RecordingHubContext : IHubContext<SagaHub, ISagaHubClient>
{
    public RecordingHubClients Recorder { get; } = new();

    public IHubClients<ISagaHubClient> Clients => Recorder;

    public IGroupManager Groups { get; } = new RecordingGroupManager();
}

internal sealed record GroupMembershipChange(string ConnectionId, string GroupName);

internal sealed class RecordingGroupManager : IGroupManager
{
    public List<GroupMembershipChange> Added { get; } = [];

    public List<GroupMembershipChange> Removed { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add(new GroupMembershipChange(connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Removed.Add(new GroupMembershipChange(connectionId, groupName));
        return Task.CompletedTask;
    }
}

/// <summary>Minimal <see cref="HubCallerContext"/> — the hub only ever reads <see cref="ConnectionId"/>.</summary>
internal sealed class TestHubCallerContext(string connectionId) : HubCallerContext
{
    public override string ConnectionId { get; } = connectionId;

    public override string? UserIdentifier => null;

    public override System.Security.Claims.ClaimsPrincipal? User => null;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
}
