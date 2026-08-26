using BugsMQ.Abstractions.Transport;

namespace BugsMQ.Transport.Wolverine;

internal enum AckOutcome
{
    Pending,
    Acked,
    Nacked,
}

/// <summary>
/// Bridges BugsMQ's explicit ack/nack contract onto Wolverine's own implicit ack-on-return/fault-on-throw
/// handler model. By the time <see cref="RawDispatchRegistry.DispatchAsync"/> resumes after awaiting the
/// downstream handler, <see cref="Outcome"/> has always already moved off <see cref="AckOutcome.Pending"/>
/// — every BugsMQ handler (SagaOrchestrator, the sample's ParticipantService) awaits its own
/// AckAsync/NackAsync before returning, exactly as it does against RabbitMqTransport.
/// </summary>
internal sealed class WolverineAckContext : IMessageAckContext
{
    public AckOutcome Outcome { get; private set; } = AckOutcome.Pending;

    public Task AckAsync(CancellationToken cancellationToken = default)
    {
        Outcome = AckOutcome.Acked;
        return Task.CompletedTask;
    }

    /// <summary>
    /// <paramref name="requeue"/> is intentionally ignored: Core owns bounded redelivery at the
    /// application level (SagaOrchestrator.HandleInfrastructureFailureAsync republishes with an
    /// incremented x-bugsmq-delivery-attempt header itself, never relying on broker-native requeue).
    /// "Nacked" here only needs to mean "settle this as rejected, don't let Wolverine's own retry policy
    /// fight with Core's" — see RawDispatchRegistry.DispatchAsync, which turns this into a thrown
    /// exception so the zero-retry OnException&lt;Exception&gt;().MoveToErrorQueue() policy configured in
    /// ServiceCollectionExtensions routes it straight to Wolverine's error queue.
    /// </summary>
    public Task NackAsync(bool requeue, CancellationToken cancellationToken = default)
    {
        Outcome = AckOutcome.Nacked;
        return Task.CompletedTask;
    }
}
