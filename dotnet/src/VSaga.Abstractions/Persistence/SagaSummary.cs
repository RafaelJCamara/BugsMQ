using VSaga.Abstractions.Sagas;

namespace VSaga.Abstractions.Persistence;

/// <summary>
/// Saga-type-agnostic projection of one instance, for the dashboard's list/detail views.
/// <para>
/// <see cref="ParentSagaType"/>/<see cref="ParentCorrelationId"/> are deliberately positional and
/// non-optional rather than defaulted: every projection site has to decide what to put there. The
/// same fields also ride along inside the snapshot's serialized state for free, but that blob is not
/// queryable, so a "which sagas did this one start?" lookup needs them projected here.
/// </para>
/// </summary>
public sealed record SagaSummary(
    Guid CorrelationId,
    string SagaType,
    SagaKind Kind,
    string CurrentState,
    SagaStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version,
    string? ParentSagaType,
    Guid? ParentCorrelationId);

public sealed class SagaListFilter
{
    public SagaStatus? Status { get; init; }

    public string? SagaType { get; init; }

    public SagaKind? Kind { get; init; }

    /// <summary>Case-insensitive substring match against SagaType and CorrelationId.</summary>
    public string? Search { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    /// <summary>Null keeps the default ordering (most-recently-updated first).</summary>
    public SagaSortColumn? SortBy { get; init; }

    public bool SortDescending { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
