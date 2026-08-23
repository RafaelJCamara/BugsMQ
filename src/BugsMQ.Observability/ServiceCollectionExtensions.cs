using BugsMQ.Abstractions.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BugsMQ.Observability;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires BugsMQ's saga ActivitySource/Meter (defined once in BugsMQ.Abstractions so Core,
    /// Persistence, and Transport packages all emit against the same names) into the app's
    /// OpenTelemetry pipeline. Exporters (OTLP/Jaeger/Prometheus/console) are the app's own choice —
    /// this method only registers the sources; it never assumes a collector is present, since the
    /// dashboard reads the persisted event log rather than an OTel backend.
    /// </summary>
    public static IServiceCollection AddBugsMqOpenTelemetry(
        this IServiceCollection services,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing.AddSource(BugsMqDiagnostics.ActivitySourceName);
                configureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(BugsMqDiagnostics.MeterName);
                configureMetrics?.Invoke(metrics);
            });

        return services;
    }
}
