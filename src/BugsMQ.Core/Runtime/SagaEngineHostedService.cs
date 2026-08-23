using BugsMQ.Abstractions.Transport;
using Microsoft.Extensions.Hosting;

namespace BugsMQ.Core.Runtime;

/// <summary>Subscribes every registered saga's declared message types to the transport at startup.</summary>
internal sealed class SagaEngineHostedService(IEnumerable<ISagaRuntime> runtimes, IMessageTransport transport) : IHostedService
{
    private readonly List<IDisposable> _subscriptions = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in runtimes)
            _subscriptions.Add(await transport.SubscribeAsync(runtime.Subscription, runtime.HandleReceivedAsync, cancellationToken));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();

        _subscriptions.Clear();
        return Task.CompletedTask;
    }
}
