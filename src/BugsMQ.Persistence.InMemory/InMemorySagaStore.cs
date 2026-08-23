using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Persistence.InMemory;

/// <summary>
/// Single backing store shared by <see cref="InMemorySagaSnapshotStore{TState}"/> (one instance per
/// saga TState, all delegating here), <see cref="ISagaSummaryReader"/>, <see cref="ISagaEventLogStore"/>,
/// <see cref="ISagaTimeoutStore"/>, and <see cref="ISagaAdminStore"/> — mirrors how the EF Core
/// provider's single DbContext backs the same contracts. Registered as a singleton. Not a toy: it
/// round-trips state through JSON on every read/write, exactly like a real persistence provider, so
/// tests exercise real snapshot isolation instead of accidentally sharing object references with the orchestrator.
/// </summary>
public sealed class InMemorySagaStore : ISagaSummaryReader, ISagaEventLogStore, ISagaTimeoutStore, ISagaAdminStore
{
    private readonly ConcurrentDictionary<Guid, StoredSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, ImmutableList<SagaLogEntry>> _timelines = new();
    private readonly ConcurrentDictionary<long, SagaTimeout> _timeouts = new();
    private long _sequence;
    private long _timeoutId;

    private sealed record StoredSnapshot(string Json, string SagaType, SagaKind Kind, string CurrentState, SagaStatus Status, int Version, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

    private static string Serialize<TState>(TState state) where TState : SagaState => JsonSerializer.Serialize(state);

    private static TState Deserialize<TState>(string json) where TState : SagaState =>
        JsonSerializer.Deserialize<TState>(json) ?? throw new InvalidOperationException("Failed to deserialize saga snapshot.");

    internal TState? Find<TState>(Guid correlationId) where TState : SagaState =>
        _snapshots.TryGetValue(correlationId, out var stored) ? Deserialize<TState>(stored.Json) : null;

    internal void Insert<TState>(TState state) where TState : SagaState
    {
        var stored = ToStored(state);
        if (!_snapshots.TryAdd(state.CorrelationId, stored))
            throw new SagaAlreadyExistsException(state.CorrelationId);
    }

    internal void Update<TState>(TState state, int expectedVersion) where TState : SagaState
    {
        while (true)
        {
            if (!_snapshots.TryGetValue(state.CorrelationId, out var current))
                throw new SagaNotFoundException(state.CorrelationId);

            if (current.Version != expectedVersion)
                throw new SagaConcurrencyException(state.CorrelationId, expectedVersion);

            state.Version = expectedVersion + 1;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var updated = ToStored(state);

            if (_snapshots.TryUpdate(state.CorrelationId, updated, current))
                return;

            // another writer beat us to it between the read and the compare-and-swap; loop and re-check version
        }
    }

    private static StoredSnapshot ToStored<TState>(TState state) where TState : SagaState =>
        new(Serialize(state), state.SagaType, state.Kind, state.CurrentState, state.Status, state.Version, state.CreatedAtUtc, state.UpdatedAtUtc);

    public Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default)
    {
        var query = _snapshots.Select(kvp => new SagaSummary(kvp.Key, kvp.Value.SagaType, kvp.Value.Kind, kvp.Value.CurrentState, kvp.Value.Status, kvp.Value.CreatedAtUtc, kvp.Value.UpdatedAtUtc, kvp.Value.Version));

        if (filter.Status is { } status)
            query = query.Where(s => s.Status == status);
        if (filter.Kind is { } kind)
            query = query.Where(s => s.Kind == kind);
        if (!string.IsNullOrWhiteSpace(filter.SagaType))
            query = query.Where(s => s.SagaType == filter.SagaType);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(s => s.SagaType.Contains(search, StringComparison.OrdinalIgnoreCase) || s.CorrelationId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = query.OrderByDescending(s => s.UpdatedAtUtc).ToList();
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Max(filter.PageSize, 1);
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return Task.FromResult(new PagedResult<SagaSummary>(items, page, pageSize, ordered.Count));
    }

    public Task<SagaSummary?> GetAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        if (!_snapshots.TryGetValue(correlationId, out var s))
            return Task.FromResult<SagaSummary?>(null);

        return Task.FromResult<SagaSummary?>(new SagaSummary(correlationId, s.SagaType, s.Kind, s.CurrentState, s.Status, s.CreatedAtUtc, s.UpdatedAtUtc, s.Version));
    }

    public Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SagaTypeInfo> types = _snapshots.Values
            .Select(s => new SagaTypeInfo(s.SagaType, s.Kind))
            .Distinct()
            .OrderBy(t => t.SagaType)
            .ToList();

        return Task.FromResult(types);
    }

    public Task<string?> GetDataJsonAsync(Guid correlationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshots.TryGetValue(correlationId, out var s) ? s.Json : null);

    public Task ResetStateAsync(Guid correlationId, string currentState, SagaStatus status, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (!_snapshots.TryGetValue(correlationId, out var current))
                throw new SagaNotFoundException(correlationId);

            // Patch the embedded JSON's CurrentState/Status by property name rather than deserializing
            // into a concrete TState (unknown here) — keeps this store genuinely saga-type-agnostic.
            var node = JsonNode.Parse(current.Json)!.AsObject();
            node["CurrentState"] = currentState;
            node["Status"] = (int)status;

            var updated = current with
            {
                Json = node.ToJsonString(),
                CurrentState = currentState,
                Status = status,
                Version = current.Version + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            if (_snapshots.TryUpdate(correlationId, updated, current))
                return Task.CompletedTask;
        }
    }

    public Task<long> AppendAsync(SagaLogEntry entry, CancellationToken cancellationToken = default)
    {
        var sequenceNumber = Interlocked.Increment(ref _sequence);
        var stamped = entry with { SequenceNumber = sequenceNumber };

        _timelines.AddOrUpdate(
            entry.CorrelationId,
            _ => ImmutableList.Create(stamped),
            (_, list) => list.Add(stamped));

        return Task.FromResult(sequenceNumber);
    }

    public Task<IReadOnlyList<SagaLogEntry>> GetTimelineAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SagaLogEntry> result = _timelines.TryGetValue(correlationId, out var list) ? list : [];
        return Task.FromResult(result);
    }

    public Task<bool> IsDuplicateAsync(Guid correlationId, string messageId, CancellationToken cancellationToken = default)
    {
        var isDuplicate = _timelines.TryGetValue(correlationId, out var list) &&
                           list.Any(e => e.MessageId == messageId);
        return Task.FromResult(isDuplicate);
    }

    public Task ScheduleAsync(Guid correlationId, string sagaType, string forState, DateTimeOffset dueAtUtc, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _timeoutId);
        _timeouts[id] = new SagaTimeout(id, correlationId, sagaType, forState, dueAtUtc, SagaTimeoutStatus.Pending);
        return Task.CompletedTask;
    }

    public Task CancelAsync(Guid correlationId, string forState, CancellationToken cancellationToken = default)
    {
        foreach (var (id, timeout) in _timeouts)
        {
            if (timeout.CorrelationId == correlationId && timeout.ForState == forState && timeout.Status == SagaTimeoutStatus.Pending)
                _timeouts[id] = timeout with { Status = SagaTimeoutStatus.Cancelled };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SagaTimeout>> ClaimDueAsync(DateTimeOffset asOf, int batchSize, CancellationToken cancellationToken = default)
    {
        var claimed = new List<SagaTimeout>();

        foreach (var (id, timeout) in _timeouts)
        {
            if (claimed.Count >= batchSize)
                break;

            if (timeout.Status != SagaTimeoutStatus.Pending || timeout.DueAtUtc > asOf)
                continue;

            var fired = timeout with { Status = SagaTimeoutStatus.Fired };
            if (_timeouts.TryUpdate(id, fired, timeout))
                claimed.Add(fired);
        }

        return Task.FromResult<IReadOnlyList<SagaTimeout>>(claimed);
    }
}
