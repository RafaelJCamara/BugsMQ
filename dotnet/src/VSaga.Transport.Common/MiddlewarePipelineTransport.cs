using VSaga.Abstractions.Transport;

namespace VSaga.Transport.Common;

/// <summary>
/// Wraps a concrete transport in the outbound/inbound middleware pipeline. Core only ever depends on
/// <see cref="IMessageTransport"/>, never on this type directly — v1 registers zero middlewares (pure
/// pass-through), but the seam exists now so the future VSaga.Chaos package can add fault-injection
/// middlewares without touching Core, Abstractions, or any concrete transport. Lives here rather than
/// in a specific transport project so every adapter (RabbitMQ, Wolverine, MassTransit, ...) can share
/// it without depending on a sibling adapter.
/// </summary>
public sealed class MiddlewarePipelineTransport(
    IMessageTransport inner,
    IEnumerable<IOutboundMessageMiddleware> outboundMiddlewares,
    IEnumerable<IInboundMessageMiddleware> inboundMiddlewares) : IMessageTransport
{
    private readonly IReadOnlyList<IOutboundMessageMiddleware> _outbound = outboundMiddlewares.ToList();
    private readonly IReadOnlyList<IInboundMessageMiddleware> _inbound = inboundMiddlewares.ToList();

    public Task PublishAsync<TMessage>(TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var context = new OutboundMessageContext(message, envelope, destinationHint: "publish");
        return RunOutboundAsync(context, ctx => ctx.Suppressed ? Task.CompletedTask : inner.PublishAsync((TMessage)ctx.Message, ctx.Envelope, cancellationToken));
    }

    public Task SendAsync<TMessage>(string destination, TMessage message, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        where TMessage : notnull
    {
        var context = new OutboundMessageContext(message, envelope, destinationHint: destination);
        return RunOutboundAsync(context, ctx => ctx.Suppressed ? Task.CompletedTask : inner.SendAsync(destination, (TMessage)ctx.Message, ctx.Envelope, cancellationToken));
    }

    public Task<IDisposable> SubscribeAsync(TransportSubscription subscription, Func<ReceivedMessage, CancellationToken, Task> handler, CancellationToken cancellationToken = default) =>
        inner.SubscribeAsync(subscription, (received, ct) => RunInboundAsync(received, handler, ct), cancellationToken);

    // Bypasses the middleware pipeline: this path is used for administrative redrive (the dashboard's
    // manual retry), not mainstream saga traffic, and it carries pre-serialized bytes rather than a
    // typed message that OutboundMessageContext.Message is shaped for.
    public Task PublishRawAsync(string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        inner.PublishRawAsync(messageTypeName, body, envelope, cancellationToken);

    // Same bypass as PublishRawAsync above, and likewise an explicit override rather than the
    // interface's default fallback, which would silently drop the destination.
    public Task SendRawAsync(string destination, string messageTypeName, ReadOnlyMemory<byte> body, MessageEnvelope envelope, CancellationToken cancellationToken = default) =>
        inner.SendRawAsync(destination, messageTypeName, body, envelope, cancellationToken);

    private Task RunOutboundAsync(OutboundMessageContext context, Func<OutboundMessageContext, Task> terminal)
    {
        var pipeline = terminal;

        for (var i = _outbound.Count - 1; i >= 0; i--)
        {
            var middleware = _outbound[i];
            var next = pipeline;
            pipeline = ctx => middleware.InvokeAsync(ctx, next);
        }

        return pipeline(context);
    }

    private Task RunInboundAsync(ReceivedMessage received, Func<ReceivedMessage, CancellationToken, Task> terminal, CancellationToken cancellationToken)
    {
        Func<InboundMessageContext, Task> pipeline = ctx => ctx.Suppressed ? Task.CompletedTask : terminal(ctx.Message, cancellationToken);

        for (var i = _inbound.Count - 1; i >= 0; i--)
        {
            var middleware = _inbound[i];
            var next = pipeline;
            pipeline = ctx => middleware.InvokeAsync(ctx, next);
        }

        return pipeline(new InboundMessageContext(received));
    }
}
