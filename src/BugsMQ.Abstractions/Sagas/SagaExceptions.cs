namespace BugsMQ.Abstractions.Sagas;

// A saga instance is identified by (SagaType, CorrelationId), so every one of these carries both —
// a bare correlation id no longer names an instance unambiguously once two saga types are allowed to
// track the same one.

public class SagaConcurrencyException(string sagaType, Guid correlationId, int expectedVersion)
    : Exception($"Saga '{sagaType}' instance '{correlationId}' was not at expected version {expectedVersion}; it was updated concurrently.")
{
    public string SagaType { get; } = sagaType;

    public Guid CorrelationId { get; } = correlationId;

    public int ExpectedVersion { get; } = expectedVersion;
}

public class SagaAlreadyExistsException(string sagaType, Guid correlationId)
    : Exception($"A '{sagaType}' saga instance with correlation id '{correlationId}' already exists.")
{
    public string SagaType { get; } = sagaType;

    public Guid CorrelationId { get; } = correlationId;
}

public class SagaNotFoundException(string sagaType, Guid correlationId)
    : Exception($"No '{sagaType}' saga instance was found with correlation id '{correlationId}'.")
{
    public string SagaType { get; } = sagaType;

    public Guid CorrelationId { get; } = correlationId;
}

public class SagaDefinitionException(string message) : Exception(message);

public class SagaRetryNotAllowedException(string sagaType, Guid correlationId, string currentStatus)
    : Exception($"Saga '{sagaType}' instance '{correlationId}' cannot be retried while its status is '{currentStatus}'; only 'Failed' sagas can be retried.")
{
    public string SagaType { get; } = sagaType;

    public Guid CorrelationId { get; } = correlationId;

    public string CurrentStatus { get; } = currentStatus;
}
