using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Abstractions.Persistence;

public sealed record SagaTypeInfo(string SagaType, SagaKind Kind);

/// <summary>
/// Non-generic, cross-saga-type read access. Powers the dashboard's saga list/detail views without
/// needing to know each saga's concrete TState type.
/// </summary>
public interface ISagaSummaryReader
{
    Task<PagedResult<SagaSummary>> ListAsync(SagaListFilter filter, CancellationToken cancellationToken = default);

    Task<SagaSummary?> GetAsync(Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Raw serialized business state (the TState JSON), for the dashboard's saga detail "Data" tab — generic access without knowing the concrete TState type.</summary>
    Task<string?> GetDataJsonAsync(Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct saga types that have ever run, derived from persisted data rather than requiring the
    /// dashboard process to register every saga definition — powers the Orchestrated/Choreographed
    /// badges and the list view's type filter dropdown.
    /// </summary>
    Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default);
}
