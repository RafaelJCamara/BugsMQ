using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BugsMQ.Abstractions.Diagnostics;

/// <summary>
/// Shared OpenTelemetry ActivitySource/Meter, defined once here so Core, Persistence, and Transport
/// packages all emit against the same names without depending on each other. BugsMQ.Observability
/// just wires these into an app's OTel exporters; the dashboard reads the persisted event log
/// instead, so it works even when no OTel collector is configured.
/// </summary>
public static class BugsMqDiagnostics
{
    public const string ActivitySourceName = "BugsMQ.Saga";
    public const string MeterName = "BugsMQ.Saga";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> SagasStarted = Meter.CreateCounter<long>("bugsmq.saga.started");
    public static readonly Counter<long> SagasCompleted = Meter.CreateCounter<long>("bugsmq.saga.completed");
    public static readonly Counter<long> SagasFailed = Meter.CreateCounter<long>("bugsmq.saga.failed");
    public static readonly Counter<long> StepRetries = Meter.CreateCounter<long>("bugsmq.saga.step.retries");
    public static readonly Histogram<double> StepDuration = Meter.CreateHistogram<double>("bugsmq.saga.step.duration", "ms");
    public static readonly Histogram<double> SagaDuration = Meter.CreateHistogram<double>("bugsmq.saga.duration", "ms");
    public static readonly UpDownCounter<long> RunningSagas = Meter.CreateUpDownCounter<long>("bugsmq.saga.running");

    public const string TagSagaType = "saga.type";
    public const string TagSagaKind = "saga.kind";
    public const string TagCorrelationId = "saga.correlation_id";
    public const string TagFromState = "saga.from_state";
    public const string TagToState = "saga.to_state";
}
