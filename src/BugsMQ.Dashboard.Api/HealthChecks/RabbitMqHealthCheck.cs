using BugsMQ.Transport.RabbitMQ;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BugsMQ.Dashboard.Api.HealthChecks;

/// <summary>Resolves <see cref="RabbitMqConnectionManager"/> lazily — see <see cref="PostgresHealthCheck"/> for why.</summary>
public sealed class RabbitMqHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var connectionManager = scope.ServiceProvider.GetService<RabbitMqConnectionManager>();
        if (connectionManager is null)
            return HealthCheckResult.Healthy("No message broker configured.");

        try
        {
            var connection = await connectionManager.GetConnectionAsync(cancellationToken);
            return connection.IsOpen
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("RabbitMQ connection is not open.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Could not connect to RabbitMQ.", ex);
        }
    }
}
