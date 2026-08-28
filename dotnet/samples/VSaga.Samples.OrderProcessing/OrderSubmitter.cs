using VSaga.Abstractions.Transport;
using VSaga.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VSaga.Samples.OrderProcessing;

/// <summary>Stands in for a real "place order" front door: submits a new order every few seconds so the dashboard has continuous, observable activity.</summary>
internal sealed class OrderSubmitter(IMessageTransport transport, ILogger<OrderSubmitter> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(8);
    private int _orderNumber;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Fire one immediately instead of waiting a full interval on startup.
        await SubmitOrderAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SubmitOrderAsync(stoppingToken);
    }

    private async Task SubmitOrderAsync(CancellationToken cancellationToken)
    {
        var orderId = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Interlocked.Increment(ref _orderNumber):D4}";
        var amount = Math.Round((decimal)(Random.Shared.NextDouble() * 480 + 20), 2);
        var order = new OrderSubmitted(orderId, $"CUST-{Random.Shared.Next(1000, 9999)}", amount);

        logger.LogInformation("Submitting {OrderId} for {Amount:C}", order.OrderId, order.Amount);

        // No VSaga correlation exists yet — the saga engine mints one on receipt since OrderSubmitted
        // is registered as an initiating event. This id is only for the envelope; the sample doesn't
        // need to track it further.
        await transport.PublishAsync(order, MessageEnvelope.From("OrderSubmitter", Guid.NewGuid()), cancellationToken);

        // docs/design/mixed-sagas.md §7: its own fresh correlation id (never the order's), so
        // MixedFulfilmentSaga never shares a correlation id with OrderSaga/PostShipmentChoreography and
        // gets a clean Saga Map of its own.
        await transport.PublishAsync(new FulfilmentRequested(orderId, amount), MessageEnvelope.From("OrderSubmitter", Guid.NewGuid()), cancellationToken);
    }
}
