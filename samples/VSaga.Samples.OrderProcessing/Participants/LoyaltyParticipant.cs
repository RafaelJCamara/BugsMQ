using VSaga.Abstractions.Transport;
using VSaga.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace VSaga.Samples.OrderProcessing.Participants;

/// <summary>
/// Awards loyalty points off the back of <see cref="OrderShipped"/>, entirely on its own initiative.
/// See <see cref="NotificationParticipant"/> for why these three post-shipment services are a
/// choreography rather than another orchestrated hop.
/// </summary>
internal sealed class LoyaltyParticipant(IMessageTransport transport, ILogger<LoyaltyParticipant> logger)
    : ParticipantService(transport, "LoyaltyService", logger)
{
    protected override string QueueName => "vsaga.participant.loyalty";

    private IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>? _handlers;

    protected override IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers =>
        _handlers ??= new Dictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>
        {
            [typeof(OrderShipped)] = async (msg, received, ct) =>
            {
                var m = (OrderShipped)msg;
                await Task.Delay(Random.Shared.Next(50, 600), ct);

                var points = Random.Shared.Next(10, 250);
                logger.LogInformation("Order {OrderId}: awarded {Points} loyalty points", m.OrderId, points);
                await ReplyAsync(new LoyaltyPointsAwarded(received.CorrelationId, m.OrderId, points), received, ct);
            },
        };
}
