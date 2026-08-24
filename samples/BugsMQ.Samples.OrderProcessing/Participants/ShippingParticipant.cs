using BugsMQ.Abstractions.Transport;
using BugsMQ.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing.Participants;

internal sealed class ShippingParticipant(IMessageTransport transport, ILogger<ShippingParticipant> logger)
    : ParticipantService(transport, "ShippingService", logger)
{
    protected override string QueueName => "bugsmq.participant.shipping";

    private IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>? _handlers;

    // A getter (not a field initializer) so the handler delegates below can call the inherited
    // ReplyAsync — field initializers run before the base constructor finishes and can't reference
    // instance members yet (CS0236); this builds the dictionary lazily on first use instead.
    protected override IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers =>
        _handlers ??= new Dictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>
        {
            [typeof(ShipOrder)] = async (msg, received, ct) =>
            {
                var m = (ShipOrder)msg;
                await Task.Delay(Random.Shared.Next(150, 500), ct);

                if (Random.Shared.NextDouble() < 0.05)
                {
                    logger.LogWarning("Order {OrderId}: carrier rejected shipment", m.OrderId);
                    await ReplyAsync(new ShipmentFailed(received.CorrelationId, m.OrderId, "Carrier rejected shipment"), received, ct);
                }
                else
                {
                    var tracking = $"TRK-{Random.Shared.Next(100000, 999999)}";
                    logger.LogInformation("Order {OrderId}: shipped with tracking {Tracking}", m.OrderId, tracking);
                    await ReplyAsync(new OrderShipped(received.CorrelationId, m.OrderId, tracking), received, ct);
                }
            },
        };
}
