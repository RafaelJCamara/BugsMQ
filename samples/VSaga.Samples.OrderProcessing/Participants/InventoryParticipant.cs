using VSaga.Abstractions.Transport;
using VSaga.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace VSaga.Samples.OrderProcessing.Participants;

internal sealed class InventoryParticipant(IMessageTransport transport, ILogger<InventoryParticipant> logger)
    : ParticipantService(transport, "InventoryService", logger)
{
    protected override string QueueName => "vsaga.participant.inventory";

    private IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>? _handlers;

    // A getter (not a field initializer) so the handler delegates below can call the inherited
    // ReplyAsync — field initializers run before the base constructor finishes and can't reference
    // instance members yet (CS0236); this builds the dictionary lazily on first use instead.
    protected override IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers =>
        _handlers ??= new Dictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>
        {
            [typeof(ReserveInventory)] = async (msg, received, ct) =>
            {
                var m = (ReserveInventory)msg;
                await Task.Delay(Random.Shared.Next(150, 500), ct);

                if (Random.Shared.NextDouble() < 0.1)
                {
                    logger.LogWarning("Order {OrderId}: out of stock", m.OrderId);
                    await ReplyAsync(new InventoryReservationFailed(received.CorrelationId, m.OrderId, "Out of stock"), received, ct);
                }
                else
                {
                    logger.LogInformation("Order {OrderId}: inventory reserved", m.OrderId);
                    await ReplyAsync(new InventoryReserved(received.CorrelationId, m.OrderId), received, ct);
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
