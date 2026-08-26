using Wolverine;

namespace VSaga.Transport.Wolverine;

/// <summary>
/// Marker message type that every single piece of VSaga traffic is carried as, as far as Wolverine's own
/// top-level handler-discovery/mediator pipeline is concerned — see IMessageTransport's doc comment on
/// why a saga-specific CLR type must never reach Wolverine's own routing. Deliberately empty: the handler
/// below never looks at the deserialized instance, only at <see cref="Envelope.Data"/> (the untouched wire
/// bytes <see cref="WolverineTransport"/> itself put there — see <see cref="WireEnvelope"/>). Public
/// (not internal) because Wolverine's runtime code generation compiles a separate dynamic assembly that
/// must be able to reference it.
/// </summary>
public sealed record RawEnvelope
{
    /// <summary>
    /// Not read by anything — see the type-level doc comment. Exists only so this type has a member,
    /// satisfying Sonar's empty-record rule (S2094), while staying honest that it deliberately carries no
    /// real data.
    /// </summary>
    public static string Marker => nameof(RawEnvelope);
}

/// <summary>
/// The one and only Wolverine-discovered handler for VSaga traffic. Every message this transport sends
/// arrives here regardless of its real VSaga message type (see <see cref="RawEnvelope"/>) and is handed
/// off to whichever <see cref="WolverineTransport.SubscribeAsync"/> caller is currently registered for the
/// listener it arrived on — see <see cref="RawDispatchRegistry"/>. Public for the same codegen-visibility
/// reason as <see cref="RawEnvelope"/>; not part of VSaga's own public API surface.
/// </summary>
public static class RawEnvelopeHandler
{
    public static Task Handle(RawEnvelope _, Envelope envelope, RawDispatchRegistry registry, CancellationToken cancellationToken) =>
        registry.DispatchAsync(envelope, cancellationToken);
}
