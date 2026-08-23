using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Transport.RabbitMQ;

/// <summary>
/// Trivial pass-through middleware proving the chaos seam works end-to-end. Not registered by
/// default — opt in via <c>services.AddSingleton&lt;IOutboundMessageMiddleware, LoggingOutboundMiddleware&gt;()</c>.
/// The future BugsMQ.Chaos package's DelayMiddleware/DropMiddleware/etc. plug in exactly the same way.
/// </summary>
public sealed class LoggingOutboundMiddleware(ILogger<LoggingOutboundMiddleware> logger) : IOutboundMessageMiddleware
{
    public async Task InvokeAsync(OutboundMessageContext context, Func<OutboundMessageContext, Task> next)
    {
        logger.LogDebug("Publishing {MessageType} (correlation {CorrelationId}) to {Destination}",
            context.Message.GetType().Name, context.Envelope.CorrelationId, context.DestinationHint);
        await next(context);
    }
}

public sealed class LoggingInboundMiddleware(ILogger<LoggingInboundMiddleware> logger) : IInboundMessageMiddleware
{
    public async Task InvokeAsync(InboundMessageContext context, Func<InboundMessageContext, Task> next)
    {
        logger.LogDebug("Received {MessageType} (correlation {CorrelationId})",
            context.Message.MessageTypeName, context.Message.CorrelationId);
        await next(context);
    }
}
