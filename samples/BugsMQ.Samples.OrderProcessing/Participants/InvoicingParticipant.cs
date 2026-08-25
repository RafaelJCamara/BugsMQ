using BugsMQ.Abstractions.Transport;
using BugsMQ.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing.Participants;

/// <summary>
/// Issues the invoice off the back of <see cref="OrderShipped"/>, entirely on its own initiative.
/// See <see cref="NotificationParticipant"/> for why these three post-shipment services are a
/// choreography rather than another orchestrated hop.
/// </summary>
internal sealed class InvoicingParticipant(IMessageTransport transport, ILogger<InvoicingParticipant> logger)
    : ParticipantService(transport, "InvoicingService", logger)
{
    protected override string QueueName => "bugsmq.participant.invoicing";

    private IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>? _handlers;

    protected override IReadOnlyDictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>> Handlers =>
        _handlers ??= new Dictionary<Type, Func<object, ReceivedMessage, CancellationToken, Task>>
        {
            [typeof(OrderShipped)] = async (msg, received, ct) =>
            {
                var m = (OrderShipped)msg;
                await Task.Delay(Random.Shared.Next(200, 1200), ct);

                var invoiceNumber = $"INV-{Random.Shared.Next(100000, 999999)}";
                logger.LogInformation("Order {OrderId}: issued invoice {Invoice}", m.OrderId, invoiceNumber);
                await ReplyAsync(new InvoiceIssued(received.CorrelationId, m.OrderId, invoiceNumber), received, ct);
            },
        };
}
