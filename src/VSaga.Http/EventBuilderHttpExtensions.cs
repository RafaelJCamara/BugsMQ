using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

namespace VSaga.Http;

/// <summary>
/// The DSL attachment point for docs/http-based-sagas.md §5.2: an ordinary extension method delegating
/// to <c>EventBuilder.Then(Func&lt;ISagaContext,TMessage,Task&gt;)</c> — the only seam an outside
/// assembly can reach (<c>EventBuilder</c>/<c>StepDefinition</c>/<c>SagaDefinitionModel</c> are all
/// sealed/internal). No change to VSaga.Core's DSL at all: a RabbitMQ-hosted saga gets <c>.CallHttp</c>
/// for free, and VSaga.Core stays free of an HttpClient dependency.
/// </summary>
public static class EventBuilderHttpExtensions
{
    public static EventBuilder<TState, TMessage> CallHttp<TState, TMessage>(
        this EventBuilder<TState, TMessage> builder,
        Action<HttpCallBuilder<TState, TMessage>> configure)
        where TState : SagaState, new()
        where TMessage : notnull
    {
        var callBuilder = new HttpCallBuilder<TState, TMessage>();
        configure(callBuilder);
        var call = callBuilder.Build();

        return builder.Then((context, message) => call.ExecuteAsync(context, message, context.CancellationToken));
    }
}
