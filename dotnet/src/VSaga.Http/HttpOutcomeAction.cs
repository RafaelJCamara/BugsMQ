using System.Text.Json;
using VSaga.Abstractions.Sagas;

namespace VSaga.Http;

/// <summary>
/// What to do with one resolved HTTP outcome (2xx / an explicit status / everything else) -- either
/// shape from docs/design/http-based-sagas.md §5.2. <see cref="DescribeReply"/> names the outcome for this
/// call's own reply timeline entry (§5.3), independent of whether applying it publishes anything.
/// </summary>
internal interface IHttpOutcomeAction<TState> where TState : SagaState, new()
{
    string DescribeReply();

    Task ApplyAsync(ISagaContext<TState> context, ReadOnlyMemory<byte> responseBody, CancellationToken cancellationToken);
}

/// <summary>
/// Message-loopback shape: the response body (or, for a network-level failure with no response at all,
/// an empty JSON object -- see the type-parameter note on <c>HttpCallBuilder.OnFailure&lt;TOut&gt;()</c>)
/// deserializes directly as <typeparamref name="TOut"/>, published via
/// <c>ISagaContext.PublishAfterCommitAsync</c> -- never <c>PublishAsync</c>, per §3.1/§5.1: publishing
/// immediately would let this call's own mapped reply re-enter the saga before this step's persist has
/// committed.
/// </summary>
internal sealed class LoopbackOutcomeAction<TState, TOut> : IHttpOutcomeAction<TState>
    where TState : SagaState, new()
    where TOut : notnull
{
    private static readonly byte[] EmptyJsonObject = "{}"u8.ToArray();

    // Case-insensitive, unlike every other JsonSerializer.Deserialize call in this repo (which round-trips
    // its own PascalCase wire format end to end): the far side here is an arbitrary REST API, per §1, and
    // real ones overwhelmingly return camelCase JSON.
    private static readonly JsonSerializerOptions ResponseOptions = new() { PropertyNameCaseInsensitive = true };

    public string DescribeReply() => typeof(TOut).Name;

    public Task ApplyAsync(ISagaContext<TState> context, ReadOnlyMemory<byte> responseBody, CancellationToken cancellationToken)
    {
        var bytes = responseBody.IsEmpty ? EmptyJsonObject.AsSpan() : responseBody.Span;
        var mapped = JsonSerializer.Deserialize<TOut>(bytes, ResponseOptions)
                     ?? throw new InvalidOperationException($"Deserializing the HTTP response as '{typeof(TOut).Name}' produced null.");

        return context.PublishAfterCommitAsync(mapped, cancellationToken);
    }
}

/// <summary>
/// Inline shape: mutates <typeparamref name="TState"/> synchronously, right here in this step -- no
/// loopback, no race, no map problem (§5.2). The step's own existing computed
/// <c>.TransitionTo(Func&lt;TState,State&lt;TState&gt;&gt;)</c>/<c>.Finalize(Func&lt;TState,SagaStatus?&gt;)</c>
/// selectors (already in EventBuilder, unchanged by this feature) are how an inline outcome actually
/// drives the saga onward -- deliberately not a second, competing transition mechanism, since
/// VSaga.Http changes nothing about VSaga.Core's DSL.
/// </summary>
internal sealed class InlineOutcomeAction<TState>(Action<TState> mutate) : IHttpOutcomeAction<TState>
    where TState : SagaState, new()
{
    public string DescribeReply() => "HTTP result (inline)";

    public Task ApplyAsync(ISagaContext<TState> context, ReadOnlyMemory<byte> responseBody, CancellationToken cancellationToken)
    {
        mutate(context.Saga);
        return Task.CompletedTask;
    }
}
