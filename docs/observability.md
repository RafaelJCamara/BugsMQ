# Observability

vSaga emits both OpenTelemetry traces/metrics and a fully persisted event log — the two are
independent and either works without the other.

## The persisted event log

Every saga instance's history is recorded as an append-only sequence of `SagaLogEntry` rows
(`ISagaEventLogStore`), viewable via the dashboard's Timeline tab or `GET
/api/sagas/{sagaType}/{correlationId}/timeline`. This is the backing data for the dashboard's Saga
Map too (see [`dashboard.md`](dashboard.md#saga-map)) — the dashboard reads this log directly rather
than an OTel backend, so **none of the OTel wiring below is required for the dashboard to work.**
Entry types include `SagaStarted`, `MessageReceived`, `MessagePublished`/`MessageSent`,
`StepSucceeded`/`StepFailed`, `CompensationStarted`/`CompensationStepSucceeded`/
`CompensationStepFailed`, `TimeoutScheduled`/`TimeoutFired`, `ChildSagaStarted`/`ChildSagaFinished`,
`UnexpectedEvent`, `DeliveryExhausted`, and `SagaCompleted`.

## Traces

`SagaOrchestrator` starts an `ActivityKind.Consumer` span for every inbound message handled, with the
parent context extracted from the message's own `traceparent`/`tracestate` headers when present.
`SagaContext.PublishInternalAsync` starts a producer span and injects the current activity context
into the outbound envelope's headers — so one trace can span an orchestrator and every participant it
talks to, across any transport, as long as the transport passes headers through losslessly (all six
adapters do; see [`transports/index.md`](transports/index.md)).

**W3C Trace Context, not a custom header.** vSaga uses the bare `traceparent`/`tracestate` header
names (not `x-vsaga-`-prefixed) specifically for interoperability — an OTel collector, a broker
plugin, or a non-vSaga consumer all expect the standard names. `VSagaDiagnostics.Inject`/
`TryExtractActivityContext` hand-roll the W3C format directly (`traceparent` is a fixed 55-character
string) rather than pulling in `OpenTelemetry.Api`, so `VSaga.Abstractions` stays free of any
`PackageReference` at all.

**A retried delivery keeps the same trace.** `SagaOrchestrator` already copies every inbound header
forward on redelivery, so `traceparent` echoes automatically — a retry of the same logical delivery
gets a `delivery.attempt` tag on its span rather than a fresh linked span, since it's the same logical
operation, not a new one.

**A failed step marks its span failed.** The consumer span's status is set to `Error` (with the
exception recorded) on the failure path, so a trace backend can distinguish a successful hop from one
that threw without needing to cross-reference the event log.

Tag names (`VSagaDiagnostics`): `saga.type`, `saga.kind`, `saga.correlation_id`, `saga.from_state`,
`saga.to_state`, `delivery.attempt`.

## Metrics

Meter name `VSaga.Saga` (`VSagaDiagnostics.Meter`):

| Instrument | Kind | Meaning |
| --- | --- | --- |
| `vsaga.saga.started` | `Counter<long>` | Incremented when a saga instance is created. |
| `vsaga.saga.completed` | `Counter<long>` | Incremented when a saga reaches `Completed`. |
| `vsaga.saga.failed` | `Counter<long>` | Incremented when a saga reaches `Failed`. |
| `vsaga.saga.step.retries` | `Counter<long>` | Incremented per step-level retry attempt. |
| `vsaga.saga.step.duration` | `Histogram<double>` (ms) | Duration of one step's execution. |
| `vsaga.saga.duration` | `Histogram<double>` (ms) | Total saga duration, recorded at the two places a saga reaches a terminal status (`HandleStepSuccessAsync`, `RecordTimeoutOutcomeAsync`) as `now - state.CreatedAtUtc`. |

**No `vsaga.saga.running` gauge — deliberately.** An `UpDownCounter` for "how many sagas are running
right now" was considered and rejected: it's process-local and non-idempotent, so a restart, a
redelivery, or a second replica desynchronizes it permanently with no way to self-correct. The correct
instrument is an `ObservableGauge` backed by `COUNT(*) WHERE Status = Running` against the store, which
needs scoped-store access from a meter callback that `VSaga.Observability` doesn't have yet — a named
follow-up, not built here. Wiring the wrong instrument (and shipping a permanently-wrong dashboard
number) was judged worse than shipping none.

## Wiring it up: `AddVSagaOpenTelemetry`

```csharp
services.AddVSagaOpenTelemetry(
    configureTracing: t => t.AddOtlpExporter(),
    configureMetrics: m => m.AddOtlpExporter());
```

This is the complete OTLP wiring — add the `OpenTelemetry.Exporter.OpenTelemetryProtocol` package and
pass `configureTracing`/`configureMetrics` delegates as shown. `AddVSagaOpenTelemetry` itself stays
unopinionated about exporters (no dependency on any specific one, and it never assumes a collector is
present) — the two delegates are exactly where an app plugs in whatever exporter(s) it wants (OTLP,
Jaeger, Prometheus, console, ...). The method also calls `Sdk.SetDefaultTextMapPropagator(...)` with
the W3C trace-context propagator, matching the wire format described above — set explicitly so a host
process (or another library) calling `SetDefaultTextMapPropagator` first with something else (e.g. B3)
can't silently disagree with what vSaga actually puts on the wire.

```csharp
services.AddVSagaOpenTelemetry();   // sources registered, propagator set — no exporter without the delegates above
```
