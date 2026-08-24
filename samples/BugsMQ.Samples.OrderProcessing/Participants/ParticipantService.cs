using System.Text.Json;
using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing.Participants;

/// <summary>
/// Base for the sample's downstream services (Inventory/Payment/Shipping). These are plain
/// IMessageTransport consumers, not sagas — they never touch BugsMQ.Core, which is the point: any
/// service that can publish/subscribe can participate in an orchestrated saga.
/// </summary>
internal abstract class ParticipantService(IMessageTransport transport, string consumerName, ILogger logger) : IHostedService
{
    private IDisposable? _subscription;

    protected abstract string QueueName { get; }

    protected abstract IReadOnlyDictionary<Type, Func<object, Guid, CancellationToken, Task>> Handlers { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var subscription = new TransportSubscription(consumerName, Handlers.Keys.ToList(), QueueName);
        _subscription = await transport.SubscribeAsync(subscription, HandleAsync, cancellationToken);
        logger.LogInformation("{Consumer} listening on {Queue}", consumerName, QueueName);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleAsync(ReceivedMessage received, CancellationToken cancellationToken)
    {
        var entry = Handlers.FirstOrDefault(h => string.Equals(h.Key.Name, received.MessageTypeName, StringComparison.Ordinal));

        if (entry.Key is null)
        {
            await received.Ack.AckAsync(cancellationToken);
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize(received.Body.Span, entry.Key)
                          ?? throw new InvalidOperationException($"Failed to deserialize {received.MessageTypeName}.");

            await entry.Value(message, received.CorrelationId, cancellationToken);
            await received.Ack.AckAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Consumer} failed handling {MessageType}", consumerName, received.MessageTypeName);
            await received.Ack.NackAsync(requeue: false, cancellationToken);
        }
    }
}
