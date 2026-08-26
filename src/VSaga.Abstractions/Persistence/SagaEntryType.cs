namespace VSaga.Abstractions.Persistence;

public enum SagaEntryType
{
    SagaStarted,
    StateEntered,
    MessageReceived,
    UnexpectedEvent,
    StepStarted,
    StepSucceeded,
    StepFailed,
    CompensationStarted,
    CompensationStepSucceeded,
    CompensationStepFailed,
    TimeoutScheduled,
    TimeoutFired,
    TimeoutCancelled,
    ManualRetryRequested,
    SagaCompleted,
    SagaCancelled,
    /// <summary>Redelivery attempts for an infrastructure-level failure were exhausted; the message was dead-lettered.</summary>
    DeliveryExhausted,

    // EntryType is persisted as a plain integer (see the InitialCreate migration), so new members must
    // always be appended here, never inserted earlier — inserting would silently reinterpret every
    // existing row's EntryType.
    /// <summary>A saga published a message with no specific destination (ctx.PublishAsync).</summary>
    MessagePublished,
    /// <summary>A saga sent a message to an explicit destination (ctx.SendAsync).</summary>
    MessageSent,

    /// <summary>A saga started a child via ctx.StartChildAsync. Distinguishes the hop from an ordinary MessagePublished.</summary>
    ChildSagaStarted,
    /// <summary>
    /// The engine (not saga code) published ChildSagaFinished to a child's parent because the child
    /// reached a terminal status via an unhandled exception or a timeout — the two cases
    /// ctx.NotifyParentAsync structurally cannot reach. Logged on the child's own timeline, symmetric to
    /// how NotifyParentAsync's own publish logs as an ordinary MessagePublished there.
    /// </summary>
    ChildSagaFinished,
}
