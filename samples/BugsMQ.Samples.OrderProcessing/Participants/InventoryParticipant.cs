using BugsMQ.Abstractions.Transport;
using BugsMQ.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing.Participants;

internal sealed class InventoryParticipant(IMessageTransport transport, ILogger<InventoryParticipant> logger)
    : ParticipantService(transport, "InventoryService", logger)
{
    protected override string QueueName => "bugsmq.participant.inventory";

    protected override IReadOnlyDictionary<Type, Func<object, Guid, CancellationToken, Task>> Handlers { get; } =
        new Dictionary<Type, Func<object, Guid, CancellationToken, Task>>
        {
            [typeof(ReserveInventory)] = async (msg, correlationId, ct) =>
            {
                var m = (ReserveInventory)msg;
                await Task.Delay(Random.Shared.Next(150, 500), ct);

                if (Random.Shared.NextDouble() < 0.1)
                {
                    logger.LogWarning("Order {OrderId}: out of stock", m.OrderId);
                    await transport.PublishAsync(new InventoryReservationFailed(correlationId, m.OrderId, "Out of stock"), MessageEnvelope.New(correlationId), ct);
                }
                else
                {
                    logger.LogInformation("Order {OrderId}: inventory reserved", m.OrderId);
                    await transport.PublishAsync(new InventoryReserved(correlationId, m.OrderId), MessageEnvelope.New(correlationId), ct);
                }
            },
            [typeof(ReleaseInventory)] = (msg, _, _) =>
            {
                var m = (ReleaseInventory)msg;
                logger.LogInformation("Order {OrderId}: inventory hold released (compensation)", m.OrderId);
                return Task.CompletedTask;
            },
        };
}
