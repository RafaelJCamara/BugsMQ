namespace BugsMQ.Core.Runtime;

/// <summary>Public entry point (e.g. for BugsMQ.Dashboard.Api) to trigger a manual whole-saga retry without depending on any single saga's generic TState.</summary>
public interface ISagaRetryDispatcher
{
    Task RetryAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default);
}

internal sealed class SagaRetryDispatcher(IEnumerable<ISagaRuntime> runtimes) : ISagaRetryDispatcher
{
    public Task RetryAsync(string sagaType, Guid correlationId, CancellationToken cancellationToken = default)
    {
        var runtime = runtimes.FirstOrDefault(r => string.Equals(r.SagaType, sagaType, StringComparison.Ordinal))
                      ?? throw new InvalidOperationException($"No saga is registered with type '{sagaType}'.");

        return runtime.RetryAsync(correlationId, cancellationToken);
    }
}
