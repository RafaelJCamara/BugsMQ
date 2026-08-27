using VSaga.Abstractions.Transport;
using VSaga.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace VSaga.Samples.OrderProcessing.Participants;

/// <summary>
/// A sibling of <see cref="InventoryParticipant"/>, not a fold into it: <c>ParticipantService</c> stamps
/// its own <c>consumerName</c> as <c>sourceService</c> on every reply, so folding stock reservation into
/// <c>InventoryService</c> would make <see cref="MixedFulfilmentSaga"/>'s own Map claim
/// <c>InventoryService</c> did the work -- defeating the point of a live-verification step whose whole
/// purpose is "look at this saga's map in isolation." A separate <c>"StockService"</c> /
/// <c>vsaga.participant.stock</c> keeps queue, topology entry, and map node disjoint from
/// <see cref="InventoryParticipant"/>'s.
/// </summary>
internal sealed class StockParticipant(IMessageTransport transport, ILogger<StockParticipant> logger)
    : ParticipantService(transport, "StockService", logger)
{
    protected override string QueueName => "vsaga.participant.stock";

    private IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>? _handlers;

    // A getter (not a field initializer) so the handler delegates below can call the inherited
    // ReplyAsync -- field initializers run before the base constructor finishes and can't reference
    // instance members yet (CS0236); this builds the dictionary lazily on first use instead.
    protected override IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers =>
        _handlers ??= new Dictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>
        {
            [typeof(ReserveStock)] = async (msg, received, ct) =>
            {
                var m = (ReserveStock)msg;
                await Task.Delay(Random.Shared.Next(150, 500), ct);

                var roll = Random.Shared.NextDouble();
                if (roll < 0.05)
                {
                    // Simulated hung stock service: never replies, mirroring PaymentParticipant's own
                    // failure-simulation shape, so MixedFulfilmentSaga's AwaitingStock timeout is what
                    // eventually moves this instance to Voiding.
                    logger.LogWarning("Order {OrderId}: stock service simulated as hung -- no reply will be sent", m.OrderId);
                    return;
                }

                if (roll < 0.2)
                {
                    logger.LogWarning("Order {OrderId}: stock unavailable", m.OrderId);
                    await ReplyAsync(new StockUnavailable(received.CorrelationId, m.OrderId, "Out of stock"), received, ct);
                }
                else
                {
                    logger.LogInformation("Order {OrderId}: stock reserved", m.OrderId);
                    await ReplyAsync(new StockReserved(received.CorrelationId, m.OrderId), received, ct);
                }
            },
            [typeof(ReleaseStock)] = (msg, _, _) =>
            {
                var m = (ReleaseStock)msg;
                logger.LogInformation("Order {OrderId}: stock hold released (compensation)", m.OrderId);
                return Task.CompletedTask;
            },
        };
}
