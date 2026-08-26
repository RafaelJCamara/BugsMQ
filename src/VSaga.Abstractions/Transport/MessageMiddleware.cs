namespace VSaga.Abstractions.Transport;

/// <summary>
/// Seam for cross-cutting behavior around outbound publish/send — logging, metrics, and (in the
/// future VSaga.Chaos package) fault injection: delay, drop, duplicate. Not implemented by anything
/// in v1 beyond a pass-through/logging example; exists so chaos policies are additive later.
/// </summary>
public interface IOutboundMessageMiddleware
{
    Task InvokeAsync(OutboundMessageContext context, Func<OutboundMessageContext, Task> nextAsync);
}

/// <summary>Seam for cross-cutting behavior around inbound message delivery — same rationale as <see cref="IOutboundMessageMiddleware"/>.</summary>
public interface IInboundMessageMiddleware
{
    Task InvokeAsync(InboundMessageContext context, Func<InboundMessageContext, Task> nextAsync);
}

public sealed class OutboundMessageContext(object message, MessageEnvelope envelope, string destinationHint)
{
    public object Message { get; } = message;

    public MessageEnvelope Envelope { get; set; } = envelope;

    /// <summary>Exchange/routing-key or queue name the message is headed to, for logging/policy matching.</summary>
    public string DestinationHint { get; } = destinationHint;

    /// <summary>Set by a middleware (e.g. a future chaos DropMiddleware) to suppress the actual send.</summary>
    public bool Suppressed { get; set; }
}

public sealed class InboundMessageContext(ReceivedMessage message)
{
    public ReceivedMessage Message { get; set; } = message;

    /// <summary>Set by a middleware to suppress delivery to the saga handler (message is still acked/dropped by the pipeline).</summary>
    public bool Suppressed { get; set; }
}
