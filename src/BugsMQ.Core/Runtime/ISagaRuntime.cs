using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Abstractions.Transport;

namespace BugsMQ.Core.Runtime;

/// <summary>Non-generic facade over <see cref="SagaOrchestrator{TState}"/> so the hosted services can hold a heterogeneous collection across saga TState types.</summary>
internal interface ISagaRuntime
{
    string SagaType { get; }

    SagaKind Kind { get; }

    TransportSubscription Subscription { get; }

    Task HandleReceivedAsync(ReceivedMessage message, CancellationToken cancellationToken);

    Task HandleTimeoutAsync(SagaTimeout timeout, CancellationToken cancellationToken);

    Task RetryAsync(Guid correlationId, CancellationToken cancellationToken);
}
