using BugsMQ.Abstractions.Transport;
using BugsMQ.Samples.OrderProcessing.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BugsMQ.Samples.OrderProcessing;

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
        var order = new OrderSubmitted(orderId, $"CUST-{Random.Shared.Next(1000, 9999)}", Math.Round((decimal)(Random.Shared.NextDouble() * 480 + 20), 2));

        logger.LogInformation("Submitting {OrderId} for {Amount:C}", order.OrderId, order.Amount);

        // No BugsMQ correlation exists yet — the saga engine mints one on receipt since OrderSubmitted
        // is registered as an initiating event. This id is only for the envelope; the sample doesn't
        // need to track it further.
        await transport.PublishAsync(order, MessageEnvelope.New(Guid.NewGuid()), cancellationToken);
    }
}
