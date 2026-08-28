# VSaga.Observability

OpenTelemetry instrumentation for vSaga: traces and metrics for saga steps, message dispatch, and the
persisted event log.

## Install

```bash
dotnet add package VSaga.Observability
```

## Usage

```csharp
services.AddVSagaOpenTelemetry();
```

## Docs

[docs/observability.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/observability.md) —
traces, metrics, the persisted event log, and OTLP wiring.

## License

MIT
