namespace VSaga.Core.Runtime;

public enum SagaOutboxMode
{
    Deferred,
    All,
}

/// <summary>
/// Tunables for the outbox's crash-recovery poller (<see cref="SagaOutboxDispatcherHostedService"/>)
/// and, once production-readiness.md §8 item 11 lands, which publishes get an outbox row at all.
/// </summary>
public sealed class SagaOutboxOptions
{
    /// <summary>
    /// <c>Deferred</c> (default): only <c>ctx.PublishAfterCommitAsync</c> calls get an outbox row —
    /// production-readiness.md §4's crash-recovery backstop for the deferred-publish queue. <c>All</c>
    /// additionally covers <c>ctx.PublishAsync</c>/<c>SendAsync</c>'s immediate publishes, by making
    /// them join the same deferred queue rather than firing inline (§8 item 11, wired via
    /// <c>SagaOrchestrator.DeferAllPublishes</c>/<c>SagaContext.RouteAsync</c>).
    /// </summary>
    public SagaOutboxMode Mode { get; set; } = SagaOutboxMode.Deferred;

    /// <summary>How often <see cref="SagaOutboxDispatcherHostedService"/> polls for Pending rows.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Max rows claimed per poll — matches <see cref="SagaTimeoutDispatcherHostedService"/>'s own batch size.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// A row younger than this is still within the window where the inline drain that wrote it is
    /// expected to mark it Dispatched itself; only a row older than this is treated as evidence of a
    /// crash between commit and drain, worth the poller republishing.
    /// </summary>
    public TimeSpan DispatchGracePeriod { get; set; } = TimeSpan.FromSeconds(30);
}
