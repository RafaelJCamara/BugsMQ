namespace BugsMQ.Abstractions.Sagas;

public class SagaConcurrencyException(Guid correlationId, int expectedVersion)
    : Exception($"Saga '{correlationId}' was not at expected version {expectedVersion}; it was updated concurrently.")
{
    public Guid CorrelationId { get; } = correlationId;

    public int ExpectedVersion { get; } = expectedVersion;
}

public class SagaAlreadyExistsException(Guid correlationId)
    : Exception($"A saga instance with correlation id '{correlationId}' already exists.")
{
    public Guid CorrelationId { get; } = correlationId;
}

public class SagaNotFoundException(Guid correlationId)
    : Exception($"No saga instance was found with correlation id '{correlationId}'.")
{
    public Guid CorrelationId { get; } = correlationId;
}

public class SagaDefinitionException(string message) : Exception(message);

public class SagaRetryNotAllowedException(Guid correlationId, string currentStatus)
    : Exception($"Saga '{correlationId}' cannot be retried while its status is '{currentStatus}'; only 'Failed' sagas can be retried.")
{
    public Guid CorrelationId { get; } = correlationId;

    public string CurrentStatus { get; } = currentStatus;
}
