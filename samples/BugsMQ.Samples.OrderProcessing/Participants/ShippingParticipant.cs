using BugsMQ.Abstractions.Transport;
using BugsMQ.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing.Participants;

internal sealed class ShippingParticipant(IMessageTransport transport, ILogger<ShippingParticipant> logger)
    : ParticipantService(transport, "ShippingService", logger)
{
    protected override string QueueName => "bugsmq.participant.shipping";

    protected override IReadOnlyDictionary<Type, Func<object, Guid, CancellationToken, Task>> Handlers { get; } =
        new Dictionary<Type, Func<object, Guid, CancellationToken, Task>>
        {
            [typeof(ShipOrder)] = async (msg, correlationId, ct) =>
            {
                var m = (ShipOrder)msg;
                await Task.Delay(Random.Shared.Next(150, 500), ct);

                if (Random.Shared.NextDouble() < 0.05)
                {
                    logger.LogWarning("Order {OrderId}: carrier rejected shipment", m.OrderId);
                    await transport.PublishAsync(new ShipmentFailed(correlationId, m.OrderId, "Carrier rejected shipment"), MessageEnvelope.New(correlationId), ct);
                }
                else
                {
                    var tracking = $"TRK-{Random.Shared.Next(100000, 999999)}";
                    logger.LogInformation("Order {OrderId}: shipped with tracking {Tracking}", m.OrderId, tracking);
                    await transport.PublishAsync(new OrderShipped(correlationId, m.OrderId, tracking), MessageEnvelope.New(correlationId), ct);
                }
            },
        };
}
