namespace VSaga.Abstractions.Sagas;

public enum SagaStatus
{
    Running,
    Completed,
    Failed,
    Compensating,
    Compensated,
    TimedOut,
    Cancelled,
}
