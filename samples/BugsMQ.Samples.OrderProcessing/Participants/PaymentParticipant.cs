using BugsMQ.Abstractions.Transport;
using BugsMQ.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing.Participants;

internal sealed class PaymentParticipant(IMessageTransport transport, ILogger<PaymentParticipant> logger)
    : ParticipantService(transport, "PaymentService", logger)
{
    protected override string QueueName => "bugsmq.participant.payment";

    protected override IReadOnlyDictionary<Type, Func<object, Guid, CancellationToken, Task>> Handlers { get; } =
        new Dictionary<Type, Func<object, Guid, CancellationToken, Task>>
        {
            [typeof(ChargePayment)] = async (msg, correlationId, ct) =>
            {
                var m = (ChargePayment)msg;
                await Task.Delay(Random.Shared.Next(150, 500), ct);

                var roll = Random.Shared.NextDouble();
                if (roll < 0.05)
                {
                    // Simulated hung payment gateway: never replies, so the saga's AwaitingPayment
                    // timeout is what eventually moves this order to Failed/TimedOut.
                    logger.LogWarning("Order {OrderId}: payment gateway simulated as hung — no reply will be sent", m.OrderId);
                    return;
                }

                if (roll < 0.2)
                {
                    logger.LogWarning("Order {OrderId}: card declined", m.OrderId);
                    await transport.PublishAsync(new PaymentFailed(correlationId, m.OrderId, "Card declined"), MessageEnvelope.New(correlationId), ct);
                }
                else
                {
                    logger.LogInformation("Order {OrderId}: payment charged", m.OrderId);
                    await transport.PublishAsync(new PaymentCharged(correlationId, m.OrderId), MessageEnvelope.New(correlationId), ct);
                }
            },
            [typeof(RefundPayment)] = (msg, _, _) =>
            {
                var m = (RefundPayment)msg;
                logger.LogInformation("Order {OrderId}: payment refunded (compensation)", m.OrderId);
                return Task.CompletedTask;
            },
        };
}
