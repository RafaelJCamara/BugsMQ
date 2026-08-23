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
}
