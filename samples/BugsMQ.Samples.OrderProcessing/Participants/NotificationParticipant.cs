using BugsMQ.Abstractions.Transport;
using BugsMQ.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing.Participants;

/// <summary>
/// Reacts to <see cref="OrderShipped"/> on its own initiative — nobody sends it a "notify the
/// customer" command. It announces what it did with <see cref="CustomerNotified"/> and is finished;
/// it neither knows nor cares that <c>PostShipmentChoreography</c> is tracking that event, or that
/// two sibling services are reacting to the same <see cref="OrderShipped"/> in parallel.
/// </summary>
internal sealed class NotificationParticipant(IMessageTransport transport, ILogger<NotificationParticipant> logger)
    : ParticipantService(transport, "NotificationService", logger)
{
    protected override string QueueName => "bugsmq.participant.notification";

    private IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>? _handlers;

    protected override IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers =>
        _handlers ??= new Dictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>
        {
            [typeof(OrderShipped)] = async (msg, received, ct) =>
            {
                var m = (OrderShipped)msg;

                // The three post-shipment services use deliberately different delay ranges so their
                // events genuinely interleave rather than arriving in a fixed order — the property the
                // choreography is built to tolerate, and one a fixed order would quietly hide.
                await Task.Delay(Random.Shared.Next(100, 900), ct);

                var channel = Random.Shared.NextDouble() < 0.5 ? "email" : "sms";
                logger.LogInformation("Order {OrderId}: notified customer via {Channel} (tracking {Tracking})", m.OrderId, channel, m.TrackingNumber);
                await ReplyAsync(new CustomerNotified(received.CorrelationId, m.OrderId, channel), received, ct);
            },
        };
}
