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
            // Serves InvoiceArchivalSaga, the sub-saga InvoiceFollowUpSaga starts — same relationship
            // SendInvoiceEmail above has to InvoiceDeliverySaga, for a different concern (filing a copy
            // for accounting, not getting it to the customer).
            [typeof(StoreInvoiceCopy)] = async (msg, received, ct) =>
            {
                var m = (StoreInvoiceCopy)msg;
                await Task.Delay(Random.Shared.Next(100, 500), ct);

                // A failure rate high enough that InvoiceArchivalSaga's Failed ending — and the
                // NotifyParentAsync report that carries it back to InvoiceFollowUpSaga — actually shows
                // up within a few minutes of running the sample, same reasoning as SendInvoiceEmail's
                // bounce rate above.
                if (Random.Shared.NextDouble() < 0.1)
                {
                    logger.LogWarning("Order {OrderId}: invoice {InvoiceNumber} archival failed", m.OrderId, m.InvoiceNumber);
                    await ReplyAsync(new InvoiceCopyStorageFailed(received.CorrelationId, m.OrderId, "Archive store unavailable"), received, ct);
                    return;
                }

                logger.LogInformation("Order {OrderId}: invoice {InvoiceNumber} archived", m.OrderId, m.InvoiceNumber);
                await ReplyAsync(new InvoiceCopyStored(received.CorrelationId, m.OrderId), received, ct);
            },
        };
}
