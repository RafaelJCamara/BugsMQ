using VSaga.Abstractions.Sagas;

namespace VSaga.Http;

/// <summary>
/// docs/design/mixed-sagas.md §4: the imperative counterpart to <see cref="EventBuilderHttpExtensions.CallHttp{TState,TMessage}"/>,
/// reachable from a <c>Compensate(state, ...)</c> delegate or a <c>TimeoutBuilder&lt;TState&gt;.Then(...)</c>
/// step -- neither of which hands this call an inbound <c>TState</c>-scoped message the
/// way an <c>EventBuilder</c> step does. One primitive covers both, so no separate <c>.CallHttp</c>
/// extension is needed on <c>TimeoutBuilder</c>.
/// </summary>
public static class SagaContextHttpExtensions
{
    /// <summary>
    /// <c>.Body(...)</c> on <see cref="HttpCallBuilder{TState}"/> takes its value eagerly, but the shared
    /// executor still invokes it lazily, once per retry attempt (see <see cref="HttpCallExecutor{TState}.ExecuteAsync"/>).
    /// That is semantically a no-op here: nothing mutates saga state between attempts (the only thing
    /// that happens between them is a delay), and the caller's own lambda has already closed over
    /// <paramref name="context"/> by the time <c>.Body(new { ... })</c> is written, so there is nothing
    /// left to defer -- eager capture is correct, not merely "unobservably so."
    /// </summary>
    public static Task CallHttpAsync<TState>(
        this ISagaContext<TState> context,
        Action<HttpCallBuilder<TState>> configure,
        CancellationToken cancellationToken = default)
        where TState : SagaState, new()
    {
        var builder = new HttpCallBuilder<TState>();
        configure(builder);
        var executor = builder.Build();

        return executor.ExecuteAsync(context, builder.BodyFactory, cancellationToken);
    }
}
