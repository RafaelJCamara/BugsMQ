using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VSaga.Abstractions.Diagnostics;
using VSaga.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace VSaga.Transport.Http;

/// <summary>
/// vSaga-aware, symmetric IMessageTransport over plain HTTP: PublishAsync/SendAsync POST to
/// docs/design/http-based-sagas.md §4.2's wire format, and a 200 response with a full header set + body is
/// itself the reply, fed back into whichever local subscriber the reply's own message type resolves
/// to. No broker underneath -- see <see cref="HttpInboundDispatcher"/> for how a reply is kept from
/// re-entering a saga while its own publishing step is still running (§3.1), and how the ambient
/// <see cref="SyncReplyCollector"/> tells an unroutable publish from inside a handler apart from a
/// routed one (§3.2).
/// </summary>
public sealed class HttpMessageTransport(
    HttpClient httpClient,
    HttpTransportOptions options,
    IHttpRouteTable routeTable,
    HttpInboundDispatcher dispatcher,
    ILogger<HttpMessageTransport> logger) : IMessageTransport
{
    public const string MessageTypeHeader = "x-vsaga-message-type";
    public const string CorrelationIdHeader = "x-vsaga-correlation-id";
    public const string MessageIdHeader = "x-vsaga-message-id";

    private const string VSagaHeaderPrefix = "x-vsaga-";

    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return PublishInternalAsync(messageType.Name, body, envelope, explicitDestination: null, cancellationToken);
    }

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var messageType = message.GetType();
        var body = JsonSerializer.SerializeToUtf8Bytes(message, messageType);
        return PublishInternalAsync(messageType.Name, body, envelope, explicitDestination: destination, cancellationToken);
    }

    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishInternalAsync(messageTypeName, body, envelope, explicitDestination: null, cancellationToken);

    public Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        PublishInternalAsync(messageTypeName, body, envelope, explicitDestination: destination, cancellationToken);

    public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        dispatcher.SubscribeAsync(subscription, handler, cancellationToken);

    /// <summary>
    /// Resolves targets to the union of configured remote routes and local subscribers (§3.3a) --
    /// unroutable only when both are empty, in which case an ambient SyncReplyCollector (present only
    /// while this call is running underneath a genuine inbound HTTP request) gets first refusal at
    /// capturing it as that request's synchronous reply (§3.2); only a message with a real destination,
    /// or one published outside any inline dispatch, ever becomes a normal send/POST or a throw.
    /// </summary>
    private async Task PublishInternalAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, string? explicitDestination, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> remoteUrls;
        if (explicitDestination is not null)
        {
            var url = routeTable.ResolveEndpointByName(explicitDestination);
            remoteUrls = url is null ? [] : [url];
        }
        else
        {
            remoteUrls = routeTable.ResolveRemoteEndpoints(messageTypeName);
        }

        // SendAsync's explicit destination bypasses Routes entirely (§4.3) and therefore the local
        // union too -- a direct address is either configured or it isn't, mirroring RabbitMqTransport's
        // SendAsync targeting a named queue with no exchange/binding lookup involved.
        var hasLocalSubscriber = explicitDestination is null && dispatcher.HasLocalSubscriber(messageTypeName);

        if (remoteUrls.Count == 0 && !hasLocalSubscriber)
        {
            var collector = SyncReplyCollectorAccessor.Current;
            if (collector is not null && collector.TryCapture(new CapturedReply(messageTypeName, body, envelope)))
                return;

            throw new MessageTransportPublishException(messageTypeName, envelope.CorrelationId, isUnroutable: true,
                new InvalidOperationException($"No HTTP route or local subscriber is configured for message type '{messageTypeName}'."));
        }

        if (hasLocalSubscriber)
        {
            dispatcher.EnqueueLocalDispatch(new ReceivedMessage(messageTypeName, envelope.CorrelationId, envelope.MessageId, body,
                envelope.Headers ?? new Dictionary<string, string>(StringComparer.Ordinal), NoOpAckContext.Instance));
        }

        if (remoteUrls.Count == 1)
        {
            await SendHttpRequestAsync(remoteUrls[0], messageTypeName, body, envelope, cancellationToken);
        }
        else if (remoteUrls.Count > 1)
        {
            await Task.WhenAll(remoteUrls.Select(url => SendHttpRequestAsync(url, messageTypeName, body, envelope, cancellationToken)));
        }
    }

    private async Task SendHttpRequestAsync(string baseUrl, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(baseUrl))
        {
            Content = new ReadOnlyMemoryContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ApplyVSagaHeaders((key, value) => request.Headers.TryAddWithoutValidation(key, value), messageTypeName, envelope);

        using var timeoutCts = new CancellationTokenSource(options.RequestTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning(ex, "{ServiceName}: publish of {MessageType} to {BaseUrl} failed", options.ServiceName, messageTypeName, baseUrl);
            throw new MessageTransportPublishException(messageTypeName, envelope.CorrelationId, isUnroutable: false, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Accepted)
                return;

            if (!response.IsSuccessStatusCode)
            {
                throw new MessageTransportPublishException(messageTypeName, envelope.CorrelationId, isUnroutable: false,
                    new HttpRequestException($"POST to {baseUrl} for '{messageTypeName}' returned {(int)response.StatusCode} {response.StatusCode}."));
            }

            await HandleSyncReplyAsync(response, messageTypeName, envelope.CorrelationId, cancellationToken);
        }
    }

    /// <summary>
    /// A 200 IS the reply (§1's decisions-already-taken, §4.2's wire format) -- fed back to whatever
    /// local subscriber the reply's own type resolves to via the dispatcher's channel, never dispatched
    /// inline from here: this call is itself running inside whatever gated dispatch published the
    /// original message, so dispatching the reply inline would either deadlock on that same
    /// correlation's gate or, worse, re-enter the saga before its own step has persisted (§3.1).
    /// </summary>
    private async Task HandleSyncReplyAsync(HttpResponseMessage response, string originalMessageType, Guid originalCorrelationId, CancellationToken cancellationToken)
    {
        var replyTypeName = GetHeaderValue(response.Headers, MessageTypeHeader);
        var replyCorrelationIdText = GetHeaderValue(response.Headers, CorrelationIdHeader);
        var replyMessageId = GetHeaderValue(response.Headers, MessageIdHeader);

        if (replyTypeName is null || replyMessageId is null || !Guid.TryParse(replyCorrelationIdText, out var replyCorrelationId))
        {
            throw new MessageTransportPublishException(originalMessageType, originalCorrelationId, isUnroutable: false,
                new InvalidOperationException("HTTP 200 reply is missing one of the required x-vsaga- headers (message-type/correlation-id/message-id)."));
        }

        var replyBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var replyHeaders = ExtractVSagaHeaders(response.Headers);

        dispatcher.EnqueueLocalDispatch(new ReceivedMessage(replyTypeName, replyCorrelationId, replyMessageId, replyBody, replyHeaders, NoOpAckContext.Instance));
    }

    private Uri BuildRequestUri(string baseUrl)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        var relativePath = options.InboundPath.TrimStart('/');
        return new Uri(baseUri, relativePath);
    }

    /// <summary>Writes the three reserved headers plus every envelope header, rejecting any value containing a raw CR/LF (docs/design/http-based-sagas.md §3.3: MessageEnvelope.Headers is open, and a saga author's value must never be able to inject a header/request-smuggle its way onto the wire).</summary>
    internal static void ApplyVSagaHeaders(Action<string, string> setHeader, string messageTypeName, MessageEnvelope envelope)
    {
        setHeader(MessageTypeHeader, messageTypeName);
        setHeader(CorrelationIdHeader, envelope.CorrelationId.ToString());
        setHeader(MessageIdHeader, envelope.MessageId);

        if (envelope.Headers is null)
            return;

        foreach (var (key, value) in envelope.Headers)
        {
            if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new ArgumentException(
                    $"Header '{key}' on message envelope contains a CR or LF character, which is not permitted in an HTTP header value.",
                    nameof(envelope));
            }

            setHeader(key, value);
        }
    }

    /// <summary>Everything <c>x-vsaga-</c>-prefixed, plus the two bare W3C trace context headers (never prefixed -- interoperability with non-vSaga consumers is the point).</summary>
    internal static IReadOnlyDictionary<string, string> ExtractVSagaHeaders(HttpHeaders headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers.Where(h =>
                     h.Key.StartsWith(VSagaHeaderPrefix, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(h.Key, VSagaDiagnostics.TraceParentHeader, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(h.Key, VSagaDiagnostics.TraceStateHeader, StringComparison.OrdinalIgnoreCase)))
            result[header.Key] = string.Join(",", header.Value);

        return result;
    }

    private static string? GetHeaderValue(HttpHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
