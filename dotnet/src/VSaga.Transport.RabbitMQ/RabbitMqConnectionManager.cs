using RabbitMQ.Client;

namespace VSaga.Transport.RabbitMQ;

/// <summary>Owns a single, lazily-created, auto-recovering connection shared by every publish and subscription.</summary>
public sealed class RabbitMqConnectionManager(RabbitMqOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.ConnectionString),
                ClientProvidedName = options.ClientProvidedName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.CloseAsync();

        _lock.Dispose();
    }
}
