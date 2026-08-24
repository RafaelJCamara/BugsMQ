namespace BugsMQ.Abstractions.Persistence;

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
}
