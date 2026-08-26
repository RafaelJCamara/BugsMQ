using VSaga.Abstractions.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace VSaga.Transport.Http;

public static class VSagaHttpEndpointExtensions
{
    /// <summary>
    /// Maps this service's inbound receive endpoint at HttpTransportOptions.InboundPath. Returns the
    /// RouteHandlerBuilder rather than mapping it unauthenticated, so callers chain
    /// <c>.RequireAuthorization()</c> themselves -- vSaga ships no auth opinion for this endpoint.
    /// </summary>
    public static RouteHandlerBuilder MapVSagaHttp(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<HttpTransportOptions>();
        return endpoints.MapPost(options.InboundPath, HandleInboundAsync);
    }

    private static async Task HandleInboundAsync(HttpContext context, HttpInboundDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var messageTypeName = context.Request.Headers[HttpMessageTransport.MessageTypeHeader].ToString();
        var messageId = context.Request.Headers[HttpMessageTransport.MessageIdHeader].ToString();
        var correlationIdText = context.Request.Headers[HttpMessageTransport.CorrelationIdHeader].ToString();

        if (string.IsNullOrEmpty(messageTypeName) || string.IsNullOrEmpty(messageId) || !Guid.TryParse(correlationIdText, out var correlationId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var bodyStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(bodyStream, cancellationToken);
        var body = bodyStream.ToArray();

        var headers = ExtractVSagaHeaders(context.Request.Headers);
        var received = new ReceivedMessage(messageTypeName, correlationId, messageId, body, headers, NoOpAckContext.Instance);

        var result = await dispatcher.DispatchInlineAsync(received, cancellationToken);

        if (result.Reply is { } reply)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            HttpMessageTransport.ApplyVSagaHeaders((key, value) => context.Response.Headers[key] = value, reply.MessageTypeName, reply.Envelope);
            await context.Response.Body.WriteAsync(reply.Body, cancellationToken);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        }
    }

    private static IReadOnlyDictionary<string, string> ExtractVSagaHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in headers)
        {
            if (key.StartsWith("x-vsaga-", StringComparison.OrdinalIgnoreCase))
                result[key] = StringValues.IsNullOrEmpty(values) ? string.Empty : values.ToString();
        }

        return result;
    }
}
