namespace VSaga.Core.Dsl;

public enum UnhandledEventPolicy
{
    /// <summary>Record an UnexpectedEvent timeline entry and otherwise ignore the message (default) — the safe choice for out-of-order/duplicate deliveries.</summary>
    LogAndIgnore,

    /// <summary>Throw, causing the orchestrator to nack/redeliver. Use only when out-of-order delivery genuinely indicates a bug.</summary>
    Throw,
}
