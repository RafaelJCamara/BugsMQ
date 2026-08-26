using VSaga.Abstractions.Sagas;

namespace VSaga.Http;

/// <summary>
/// Fluent configuration for one <c>.CallHttp(...)</c> call, built by the delegate passed to
/// <see cref="EventBuilderHttpExtensions.CallHttp{TState,TMessage}"/>. Mirrors
/// docs/http-based-sagas.md §5.2's target shape: <c>.Post(url).Body(...)</c> plus either result shape —
/// <c>.OnSuccess&lt;TOut&gt;()</c>/<c>.OnStatus(code).As&lt;TOut&gt;()</c>/<c>.OnFailure&lt;TOut&gt;()</c>
/// for message-loopback, or the <see cref="Action{TState}"/> overloads for inline.
/// </summary>
public sealed class HttpCallBuilder<TState, TMessage>
    where TState : SagaState, new()
    where TMessage : notnull
{
    private string? _url;
    private Func<ISagaContext<TState>, TMessage, object>? _bodyFactory;
    private readonly Dictionary<int, IHttpOutcomeAction<TState>> _statusActions = [];
    private IHttpOutcomeAction<TState>? _successAction;
    private IHttpOutcomeAction<TState>? _failureAction;
    private int _maxAttempts = 1;
    private TimeSpan _retryDelay = TimeSpan.Zero;

    public HttpCallBuilder<TState, TMessage> Post(string url)
    {
        _url = url;
        return this;
    }

    public HttpCallBuilder<TState, TMessage> Body<TBody>(Func<ISagaContext<TState>, TMessage, TBody> factory) where TBody : notnull
    {
        _bodyFactory = (context, message) => factory(context, message);
        return this;
    }

    /// <summary>Message loopback for any 2xx not covered by a more specific <see cref="OnStatus"/>. See <see cref="LoopbackOutcomeAction{TState,TOut}"/>.</summary>
    public HttpCallBuilder<TState, TMessage> OnSuccess<TOut>() where TOut : notnull
    {
        _successAction = new LoopbackOutcomeAction<TState, TOut>();
        return this;
    }

    /// <summary>Inline result shape for any 2xx not covered by a more specific <see cref="OnStatus"/>: mutates <typeparamref name="TState"/> directly, synchronously.</summary>
    public HttpCallBuilder<TState, TMessage> OnSuccess(Action<TState> mutate)
    {
        _successAction = new InlineOutcomeAction<TState>(mutate);
        return this;
    }

    /// <summary>
    /// Message loopback for anything else: a non-2xx status with no more specific <see cref="OnStatus"/>
    /// entry, or a network-level failure (timeout/<c>HttpRequestException</c>) with no response at all.
    /// <typeparamref name="TOut"/> must tolerate deserializing from an empty JSON object (<c>{}</c>) for
    /// the no-response case — there is no body to hydrate a genuine failure reason from.
    /// </summary>
    public HttpCallBuilder<TState, TMessage> OnFailure<TOut>() where TOut : notnull
    {
        _failureAction = new LoopbackOutcomeAction<TState, TOut>();
        return this;
    }

    /// <summary>Inline result shape for anything else — see the loopback overload's remarks on when this fires.</summary>
    public HttpCallBuilder<TState, TMessage> OnFailure(Action<TState> mutate)
    {
        _failureAction = new InlineOutcomeAction<TState>(mutate);
        return this;
    }

    /// <summary>An exact status code, taking priority over the 2xx/everything-else buckets above.</summary>
    public HttpStatusBuilder<TState, TMessage> OnStatus(int statusCode) => new(this, statusCode);

    internal void SetStatusAction(int statusCode, IHttpOutcomeAction<TState> action) => _statusActions[statusCode] = action;

    /// <summary>
    /// This call's own bounded retry for a transient network-level failure -- deliberately not
    /// <c>EventBuilder.Retry(RetryPolicy)</c>, which replays every one of this step's actions from index
    /// 0 on any throw (<c>StepExecutor.RunAsync</c>), re-POSTing this call along with everything else in
    /// the step. Only a genuine network failure (<c>HttpRequestException</c>/timeout) is retried; a
    /// definitive HTTP response -- even a 5xx -- is a real answer and is never retried, it's mapped via
    /// <see cref="OnStatus"/>/<see cref="OnFailure{TOut}"/> instead. Defaults to a single attempt (no
    /// retry).
    /// </summary>
    public HttpCallBuilder<TState, TMessage> WithRetry(int maxAttempts, TimeSpan delay)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");

        _maxAttempts = maxAttempts;
        _retryDelay = delay;
        return this;
    }

    internal HttpCallDefinition<TState, TMessage> Build()
    {
        if (_url is null)
            throw new InvalidOperationException("A .CallHttp(...) configuration needs a target URL -- call .Post(url) first.");

        return new HttpCallDefinition<TState, TMessage>(_url, _bodyFactory, _statusActions, _successAction, _failureAction, _maxAttempts, _retryDelay);
    }
}

/// <summary>The half of the DSL scoped to one exact status code, returned by <see cref="HttpCallBuilder{TState,TMessage}.OnStatus"/>.</summary>
public sealed class HttpStatusBuilder<TState, TMessage>(HttpCallBuilder<TState, TMessage> parent, int statusCode)
    where TState : SagaState, new()
    where TMessage : notnull
{
    /// <summary>Message loopback for this exact status — see <see cref="LoopbackOutcomeAction{TState,TOut}"/>.</summary>
    public HttpCallBuilder<TState, TMessage> As<TOut>() where TOut : notnull
    {
        parent.SetStatusAction(statusCode, new LoopbackOutcomeAction<TState, TOut>());
        return parent;
    }

    /// <summary>Inline result shape for this exact status: mutates <typeparamref name="TState"/> directly, synchronously.</summary>
    public HttpCallBuilder<TState, TMessage> Then(Action<TState> mutate)
    {
        parent.SetStatusAction(statusCode, new InlineOutcomeAction<TState>(mutate));
        return parent;
    }
}
