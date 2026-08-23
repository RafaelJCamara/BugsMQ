using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Abstractions.Persistence;

public sealed record SagaSummary(
    Guid CorrelationId,
    string SagaType,
    SagaKind Kind,
    string CurrentState,
    SagaStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version);

public sealed class SagaListFilter
{
    public SagaStatus? Status { get; init; }

    public string? SagaType { get; init; }

    public SagaKind? Kind { get; init; }

    /// <summary>Case-insensitive substring match against SagaType and CorrelationId.</summary>
    public string? Search { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
