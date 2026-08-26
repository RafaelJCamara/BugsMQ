using System.Net.Http.Headers;
using System.Text.Json;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace VSaga.Http;

/// <summary>
/// The immutable, built form of one <c>.CallHttp(...)</c> configuration -- everything
/// <see cref="HttpCallBuilder{TState,TMessage}"/> collected, executed once per step invocation.
/// Transport-agnostic and independent of whichever <c>IMessageTransport</c> the host saga is wired to:
/// this makes a real outbound REST call over its own <see cref="HttpClient"/>, then feeds the result
/// back through <see cref="ISagaContext{TState}"/> exactly like any other step action.
/// </summary>
internal sealed class HttpCallDefinition<TState, TMessage>(
    string url,
    Func<ISagaContext<TState>, TMessage, object>? bodyFactory,
    IReadOnlyDictionary<int, IHttpOutcomeAction<TState>> statusActions,
    IHttpOutcomeAction<TState>? successAction,
    IHttpOutcomeAction<TState>? failureAction,
    int maxAttempts,
    TimeSpan retryDelay)
    where TState : SagaState, new()
    where TMessage : notnull
{
    private readonly string _host = new Uri(url, UriKind.Absolute).Host;

    public async Task ExecuteAsync(ISagaContext<TState> context, TMessage message, CancellationToken cancellationToken)
    {
        var httpClient = context.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.ClientName);
        var log = (ISagaContextLogSink)context;
        var sagaType = context.Saga.SagaType;

        // This call's own outbound leg, minted independently of whatever ISagaContext.PublishAfterCommitAsync
        // logs for a loopback outcome below -- the whole point of §5.3's fix. Logged before sending so the
        // Timeline reads as "sent" then "received" even if the call never returns.
        var callId = Guid.NewGuid().ToString("N");
        await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.MessagePublished,
            messageType: $"POST {url}", messageId: callId, sourceService: sagaType, destinationService: _host), cancellationToken);

        var (statusCode, body, transportError) = await SendWithRetryAsync(httpClient, context, message, cancellationToken);
        var action = ResolveAction(statusCode);

        await log.LogAsync(SagaLogEntry.Create(context.CorrelationId, sagaType, SagaEntryType.MessageReceived,
            messageType: action.DescribeReply(), messageId: Guid.NewGuid().ToString("N"),
            sourceService: _host, causationId: callId, errorMessage: transportError?.Message), cancellationToken);

        await action.ApplyAsync(context, body, cancellationToken);
    }

    private async Task<(int? StatusCode, ReadOnlyMemory<byte> Body, Exception? TransportError)> SendWithRetryAsync(
        HttpClient httpClient, ISagaContext<TState> context, TMessage message, CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (bodyFactory is not null)
            {
                var body = bodyFactory(context, message);
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, body.GetType()));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            try
            {
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return ((int)response.StatusCode, responseBody, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                    await Task.Delay(retryDelay, cancellationToken);
            }
        }

        return (null, ReadOnlyMemory<byte>.Empty, lastError);
    }

    /// <summary>Exact status first, then the 2xx/everything-else buckets — see docs/http-based-sagas.md §5.4's mapping table. Unconfigured is a DSL error, not a silent no-op.</summary>
    private IHttpOutcomeAction<TState> ResolveAction(int? statusCode)
    {
        if (statusCode is { } code && statusActions.TryGetValue(code, out var exact))
            return exact;

        if (statusCode is >= 200 and <= 299)
        {
            return successAction
                   ?? throw new InvalidOperationException($".CallHttp to '{url}' received status {statusCode} but no .OnSuccess(...) was configured.");
        }

        return failureAction ?? throw new InvalidOperationException(statusCode is { } failedStatus
            ? $".CallHttp to '{url}' received status {failedStatus} but no .OnFailure(...) was configured."
            : $".CallHttp to '{url}' failed (network error or timeout) but no .OnFailure(...) was configured.");
    }
}
