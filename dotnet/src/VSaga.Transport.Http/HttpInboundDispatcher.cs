using System.Collections.Concurrent;
using System.Threading.Channels;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace VSaga.Transport.Http;

/// <summary>
/// Owns the local subscriber registry (populated by SubscribeAsync, matched exactly like
/// InMemoryMessageTransport.DispatchAsync) and the per-correlation-id dispatch gate that is this
/// adapter's whole answer to docs/design/http-based-sagas.md §3.1: a reply must never re-enter a saga while
/// its own step is still running.
/// <para>
/// Exactly two entry points ever reach a local subscriber, and the asymmetry between them is the
/// entire §3.1 answer:
/// </para>
/// <list type="bullet">
/// <item><see cref="DispatchInlineAsync"/> -- a genuine inbound HTTP request. Dispatched immediately,
/// holding the gate, because the handler's reply has to be captured before the response is written --
/// unless the gate can't be acquired within <see cref="InlineGateAcquireTimeout"/>, in which case it
/// falls back to the deferred path below rather than blocking the connection for the full
/// RequestTimeout (found live: a fan-out reply that routes back to its own originating service can
/// otherwise deadlock that service's gate against itself).</item>
/// <item><see cref="EnqueueLocalDispatch"/> -- everything else that resolves to a local subscriber: a
/// same-process PublishAsync/PublishRawAsync (including §3.3a's redelivery, which runs from *inside*
/// an already-gated dispatch) and a 200 reply to our own outbound POST. Never dispatched inline --
/// always enqueued to <see cref="_localDispatchChannel"/> and drained by <see cref="PumpLoopAsync"/>,
/// which takes the same gate. That is what lets a redelivery enqueue itself without deadlocking on the
/// gate its own catch block is running inside, and what makes a reply wait for the publishing step to
/// finish (i.e. until after PersistAsync and the ack) before it can be dispatched.
/// </item>
/// </list>
/// </summary>
public sealed class HttpInboundDispatcher : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SubscriberEntry> _subscribers = new();

    // Keyed on the raw transport correlation id, not any resolved saga instance -- production-readiness.md
    // §5.4's documented, accepted gap: two messages resolving to the same saga via a shared business key
    // (§5.2/§5.3) but carrying different transport correlation ids get independent gate entries here and
    // run fully concurrently. Pinned by HttpInboundDispatcherGateHazardTests (VSaga.Transport.Http.Tests).
    // The backstop for that case -- the snapshot store's optimistic-concurrency Version check, and that a
    // SagaConcurrencyException from it reliably reaches SagaOrchestrator.HandleInfrastructureFailureAsync's
    // redelivery rather than being swallowed -- is verified separately by
    // SagaOrchestratorConcurrencyRedeliveryTests (VSaga.Core.Tests). Change either exception path or this
    // key without re-checking both; §5.4 has the full account of what that backstop does and does not
    // actually guarantee.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _correlationGates = new();
    private readonly Channel<ReceivedMessage> _localDispatchChannel = Channel.CreateUnbounded<ReceivedMessage>();
    private readonly ILogger<HttpInboundDispatcher> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pumpTask;

    public HttpInboundDispatcher(ILogger<HttpInboundDispatcher> logger)
    {
        _logger = logger;
        _pumpTask = Task.Run(() => PumpLoopAsync(_stopping.Token), _stopping.Token);
    }

    private sealed record SubscriberEntry(TransportSubscription Subscription, Func<ReceivedMessage, CancellationToken, Task> Handler);

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = new SubscriberEntry(subscription, handler);
        return Task.FromResult<IDisposable>(new Unsubscriber(() => _subscribers.TryRemove(id, out _)));
    }

    /// <summary>Whether any locally-registered subscription declares this message type -- the local half of §3.3a's routing union.</summary>
    public bool HasLocalSubscriber(string messageTypeName) =>
        _subscribers.Values.Any(s => MatchesType(s.Subscription, messageTypeName));

    /// <summary>Queues a message for local dispatch without blocking on the correlation gate -- see the type doc for why this, never inline, is the only path other than a genuine inbound request.</summary>
    public void EnqueueLocalDispatch(ReceivedMessage received) =>
        _localDispatchChannel.Writer.TryWrite(received);

    /// <summary>
    /// Bound on acquiring the correlation gate for a genuine inbound request before giving up and
    /// deferring to the pump instead of continuing to block the HTTP connection. Found live: a fan-out
    /// reply that routes back to its own originating service (e.g. OrderShipped reaching both its local
    /// participants and back to the saga host) can deadlock that service's own gate against itself --
    /// the saga's dispatch holds the gate while awaiting ShipOrder's response, and Participants can't
    /// finish answering ShipOrder until its own nested OrderShipped POST back to the saga host is
    /// accepted, which needs the very gate the saga is still holding. Deferring after a short bound
    /// breaks the cycle losslessly (202 now, dispatched once the gate frees) instead of blocking for
    /// the full RequestTimeout.
    /// </summary>
    private static readonly TimeSpan InlineGateAcquireTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The one inline path: a genuine inbound HTTP request, dispatched immediately under an ambient reply collector so a synchronous reply can be captured before the caller's response is written.</summary>
    public async Task<InlineDispatchResult> DispatchInlineAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        var gate = _correlationGates.GetOrAdd(received.CorrelationId, static _ => new SemaphoreSlim(1, 1));
        var acquired = await gate.WaitAsync(InlineGateAcquireTimeout, cancellationToken);

        if (!acquired)
        {
            _logger.LogWarning(
                "Could not acquire the dispatch gate for correlation {CorrelationId} within {Timeout} -- deferring {MessageType} to the local dispatch queue instead of blocking this request",
                received.CorrelationId, InlineGateAcquireTimeout, received.MessageTypeName);
            EnqueueLocalDispatch(received);
            return InlineDispatchResult.Accepted;
        }

        var collector = new SyncReplyCollector();
        SyncReplyCollectorAccessor.Current = collector;
        try
        {
            await RunSubscribersAsync(received, cancellationToken);
        }
        finally
        {
            collector.Seal();
            SyncReplyCollectorAccessor.Current = null;
            ReleaseGate(received.CorrelationId, gate);
        }

        return collector.Captured is { } reply ? InlineDispatchResult.WithReply(reply) : InlineDispatchResult.Accepted;
    }

    /// <summary>
    /// Drains the channel and fans each item out as its own fire-and-forget dispatch rather than
    /// awaiting one before reading the next -- correctness for a single correlation id comes entirely
    /// from the gate in <see cref="DispatchToSubscribersAsync"/>, not from pump ordering, so an
    /// unrelated correlation's dispatch is never held up behind a slow one.
    /// </summary>
    private async Task PumpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var received in _localDispatchChannel.Reader.ReadAllAsync(cancellationToken))
            {
                _ = DispatchAndLogAsync(received, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DispatchAndLogAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        try
        {
            await DispatchToSubscribersAsync(received, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error draining local dispatch for {MessageType} correlation {CorrelationId}",
                received.MessageTypeName, received.CorrelationId);
        }
    }

    /// <summary>
    /// Acquires the per-correlation gate, then invokes every matching subscriber's handler in turn,
    /// each independently caught and logged -- mirroring RabbitMqTransport's dispatch-level catch
    /// (log + drop rather than propagate) so one failing subscriber can't take down a sibling's fan-out
    /// delivery of the same message, exactly as if each had its own broker-bound queue.
    /// </summary>
    private async Task DispatchToSubscribersAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        var gate = _correlationGates.GetOrAdd(received.CorrelationId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await RunSubscribersAsync(received, cancellationToken);
        }
        finally
        {
            ReleaseGate(received.CorrelationId, gate);
        }
    }

    /// <summary>Invokes every matching subscriber's handler in turn, each independently caught and logged -- mirroring RabbitMqTransport's dispatch-level catch (log + drop rather than propagate) so one failing subscriber can't take down a sibling's fan-out delivery of the same message, exactly as if each had its own broker-bound queue. Assumes the caller already holds this correlation's gate.</summary>
    private async Task RunSubscribersAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            if (!MatchesType(subscriber.Subscription, received.MessageTypeName))
                continue;

            try
            {
                await subscriber.Handler(received, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error dispatching {MessageType} to consumer {ConsumerName} for correlation {CorrelationId}",
                    received.MessageTypeName, subscriber.Subscription.ConsumerName, received.CorrelationId);
            }
        }
    }

    private void ReleaseGate(Guid correlationId, SemaphoreSlim gate)
    {
        gate.Release();

        // Best-effort cleanup: only removes the entry if it's uncontended at this exact moment, which
        // is safe either way -- see the type's remarks in docs/design/http-based-sagas.md §4.4 for why a
        // benign TOCTOU race here can't strand a waiter (an uncontended semaphore has none).
        if (gate.CurrentCount == 1)
            _correlationGates.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(correlationId, gate));
    }

    private static bool MatchesType(TransportSubscription subscription, string messageTypeName) =>
        subscription.MessageTypes.Any(t => string.Equals(t.Name, messageTypeName, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        _localDispatchChannel.Writer.TryComplete();
        await _stopping.CancelAsync();

        try
        {
            await _pumpTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _stopping.Dispose();
    }
}

public readonly record struct CapturedReply(string MessageTypeName, ReadOnlyMemory<byte> Body, MessageEnvelope Envelope);

public readonly struct InlineDispatchResult
{
    public static readonly InlineDispatchResult Accepted;

    public CapturedReply? Reply { get; private init; }

    public static InlineDispatchResult WithReply(CapturedReply reply) => new() { Reply = reply };
}

/// <summary>
/// Ambient (AsyncLocal) collector installed by the receive endpoint for the duration of one inline
/// dispatch -- the only seam available to intercept a handler's ordinary PublishAsync call and capture
/// it as that same request's synchronous reply (docs/design/http-based-sagas.md §3.2). Always a fresh instance
/// per request, never shared/static, and sealed once the response has been written so a handler's
/// `_ = Task.Run(...)` fire-and-forget -- which inherits the AsyncLocal via its captured
/// ExecutionContext -- falls through to a real publish attempt afterward instead of writing into a
/// completed collector.
/// </summary>
public sealed class SyncReplyCollector
{
    private readonly Lock _lock = new();
    private bool _sealed;

    public CapturedReply? Captured { get; private set; }

    /// <summary>True if this call captured <paramref name="reply"/> as the reply; false if the collector is sealed or already holds one -- the caller must then throw MessageTransportPublishException instead (a second unroutable message, or a post-response publish).</summary>
    public bool TryCapture(CapturedReply reply)
    {
        lock (_lock)
        {
            if (_sealed || Captured is not null)
                return false;

            Captured = reply;
            return true;
        }
    }

    public void Seal()
    {
        lock (_lock)
            _sealed = true;
    }
}

public static class SyncReplyCollectorAccessor
{
    private static readonly AsyncLocal<SyncReplyCollector?> Ambient = new();

    public static SyncReplyCollector? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }
}

/// <summary>No broker underneath means no delivery guarantee to ack/nack against -- see docs/design/http-based-sagas.md §4.4: the in-process channel is not durable, and a saga's own state timeout is the safety net, exactly as it already is for a lost broker message.</summary>
public sealed class NoOpAckContext : IMessageAckContext
{
    public static readonly NoOpAckContext Instance = new();

    public Task AckAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task NackAsync(bool requeue, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
