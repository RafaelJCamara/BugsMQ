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

    Task<SagaSummary?> GetAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>Raw serialized business state (the TState JSON), for the dashboard's saga detail "Data" tab — generic access without knowing the concrete TState type.</summary>
    Task<string?> GetDataJsonAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every saga instance sharing <paramref name="correlationId"/>, across all saga types. Powers the
    /// dashboard's "this correlation id is also tracked by N other sagas" cross-links, and is what a
    /// caller holding only a correlation id (e.g. an old bookmarked URL) uses to resolve it to a
    /// concrete instance. Ordinarily returns exactly one; more than one means several saga types are
    /// tracking the same business transaction.
    /// </summary>
    Task<IReadOnlyList<SagaSummary>> FindByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every saga instance started by <paramref name="parentSagaType"/>/<paramref name="parentCorrelationId"/>
    /// via <c>ISagaContext.StartChildAsync</c>, oldest first. Empty for the usual case of a saga that
    /// started none.
    /// <para>
    /// One level only — a grandchild is a child of its own parent and is not returned here. Callers
    /// wanting a whole tree walk it themselves; nothing in the engine limits the depth, so a recursive
    /// query would need its own cycle handling and that is not something a single instance lookup should
    /// be quietly doing.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SagaSummary>> FindChildrenAsync(string parentSagaType, Guid parentCorrelationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Distinct saga types that have ever run, derived from persisted data rather than requiring the
    /// dashboard process to register every saga definition — powers the Orchestrated/Choreographed
    /// badges and the list view's type filter dropdown.
    /// </summary>
    Task<IReadOnlyList<SagaTypeInfo>> GetSagaTypesAsync(CancellationToken cancellationToken = default);
}
