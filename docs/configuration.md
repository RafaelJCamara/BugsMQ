# Configuration

Every options class below binds from `IConfiguration` the standard .NET way (`services.Configure<T>(configuration.GetSection("..."))`)
or is set directly in code when calling the relevant `AddVSaga*` extension. Defaults are shown as
written in source.

## `SagaOrchestratorOptions`

Registered by `AddVSagaEngine(...)`. One tunable:

| Property | Default | Meaning |
| --- | --- | --- |
| `MaxDeliveryAttempts` | `5` | How many times an **infrastructure-level** failure (a deserialize error, a persistence-store exception — distinct from a saga step's own thrown exception, which `HandleStepFailureAsync` already handles by marking the saga `Failed`) redelivers the same message, with an incremented `x-vsaga-delivery-attempt` header, before it is routed to the dead-letter queue instead of requeued forever. |

## `SagaOutboxOptions`

Registered by `AddVSagaEngine(...)`. Governs the transactional outbox's crash-recovery poller
(`SagaOutboxDispatcherHostedService`) and which publishes get an outbox row in the first place.

| Property | Default | Meaning |
| --- | --- | --- |
| `Mode` | `SagaOutboxMode.Deferred` | `Deferred`: only `ctx.PublishAfterCommitAsync` calls get an outbox row (the crash-recovery backstop for the deferred-publish queue). `All`: additionally covers `ctx.PublishAsync`/`SendAsync`'s immediate publishes, by routing them through the same deferred queue `PublishAfterCommitAsync` uses — see the trade-off note below. |
| `PollInterval` | `5s` | How often the poller checks for `Pending` outbox rows. |
| `BatchSize` | `50` | Max rows claimed per poll. |
| `DispatchGracePeriod` | `30s` | A row younger than this is still within the window where the inline drain that wrote it is expected to mark it `Dispatched` itself; only a row older than this is treated as evidence of a crash between commit and drain, worth the poller republishing. |

**`Deferred` (the default) preserves today's inline publish semantics for every existing call site and
test** — `ctx.PublishAsync`/`SendAsync` still fire mid-step, immediately, exactly as before. `All` is a
deliberate trade-off, not a strict improvement: because `ctx.PublishAsync`/`SendAsync` fire mid-step
with no queuing under `Deferred`, the only way to route them through the outbox at all is to defer
them too — a row written beside a message that's already gone over the wire guarantees nothing. Under
`All`, a step that publishes and then throws no longer leaks that publish (the failure path discards
the deferred queue), but an operator choosing `All` is knowingly accepting
`ISagaContext.PublishAfterCommitAsync`'s own documented trade-off: a deferred publish that fails
post-commit has nowhere safe to go, and is caught, logged, and recorded as a `DeliveryExhausted`
timeline entry rather than retried or thrown.

## Transport options

Every `IMessageTransport` adapter has its own options class, registered by its own
`AddVSaga<Adapter>(...)` extension. `ConnectionString`/`ExchangeName` default identically across the
RabbitMQ-family adapters so switching providers is close to a drop-in config change.

### `RabbitMqOptions` (`VSaga.Transport.RabbitMQ`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ClientProvidedName` | `VSaga` |
| `ExchangeName` | `vsaga.saga.events` |
| `DeadLetterExchangeName` | `vsaga.dlx` |

### `WolverineTransportOptions` (`VSaga.Transport.Wolverine`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ExchangeName` | `vsaga.saga.events` |

### `MassTransitOptions` (`VSaga.Transport.MassTransit`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ExchangeName` | `vsaga.saga.events` |

### `BrighterOptions` (`VSaga.Transport.Brighter`)

| Property | Default |
| --- | --- |
| `ConnectionString` | `amqp://guest:guest@localhost:5672/` |
| `ClientProvidedName` | `VSaga` |
| `ExchangeName` | `vsaga.saga.events` |

### `HttpTransportOptions` (`VSaga.Transport.Http`)

No broker at all — see [`transports/http.md`](transports/http.md) for the full model.

| Property | Default | Meaning |
| --- | --- | --- |
| `ServiceName` | `vsaga-http` | This process's own identity, for logging only — never stamped onto envelopes (that's `MessageEnvelope.From`'s job). |
| `Endpoints` | `{}` | Endpoint name → base URL, e.g. `{"payments": "http://payments:8080"}`. |
| `Routes` | `{}` | Message type name → endpoint names to POST to on publish. A `"*"` key is a wildcard fallback for any type with no explicit entry. |
| `RequestTimeout` | `30s` | Per-request timeout for the outbound HTTP call, including the participant's own processing time. |
| `InboundPath` | `/vsaga/messages` | Path this service's own receive endpoint is mapped to by `MapVSagaHttp()`. |

The in-memory transport (`VSaga.Transport.InMemory`, `AddVSagaInMemoryTransport()`) takes no options —
it's a single-process, dev/test-only provider with nothing to configure.

## `ChaosOptions` (`VSaga.Chaos`)

Registered by `AddVSagaChaos(...)`. See [`chaos.md`](chaos.md) for the full fault model; the shape:

```
ChaosOptions
  Delay:     Enabled, ApplyToOutbound, ApplyToInbound, Probability, MinDelay, MaxDelay
  Drop:      Enabled, ApplyToOutbound, ApplyToInbound, Probability
  Duplicate: Enabled, ApplyToOutbound, ApplyToInbound, Probability, ExtraDeliveries
```

| Fault | Defaults |
| --- | --- |
| `Delay` | `Enabled=false`, `ApplyToOutbound=true`, `ApplyToInbound=true`, `Probability=0.1`, `MinDelay=200ms`, `MaxDelay=2s` |
| `Drop` | `Enabled=false`, `ApplyToOutbound=true`, `ApplyToInbound=true`, `Probability=0.05` |
| `Duplicate` | `Enabled=false`, `ApplyToOutbound=true`, `ApplyToInbound=true`, `Probability=0.05`, `ExtraDeliveries=1` |

Each fault is independently gated by its own `Enabled` flag; a disabled fault is never registered into
the middleware pipeline at all — no runtime check, no cost.

## Dashboard authentication

`Dashboard:ApiKey` (a plain string in configuration) is the one shared secret
`ApiKeyAuthenticationHandler` checks — see [`dashboard.md`](dashboard.md#authentication) for the full
three-places-it-can-arrive model and why the dashboard fails closed on an unconfigured key.

## OpenTelemetry: `AddVSagaOpenTelemetry`

`VSaga.Observability.ServiceCollectionExtensions.AddVSagaOpenTelemetry(this IServiceCollection,
Action<TracerProviderBuilder>? configureTracing = null, Action<MeterProviderBuilder>?
configureMetrics = null)` wires vSaga's shared `ActivitySource`/`Meter` (`VSaga.Saga`, defined once in
`VSaga.Abstractions` so Core, Persistence, and Transport all emit against the same names) into the
app's OpenTelemetry pipeline, and registers the W3C trace-context propagator as the SDK default (the
wire format vSaga's own `traceparent`/`tracestate` handling uses, so a host that set a different
default propagator first — e.g. B3 — would otherwise silently disagree with what actually goes out on
the wire).

It never assumes an OTel collector is present — the dashboard reads the persisted event log instead of
an OTel backend, so nothing here is required for the dashboard to work. See
[`observability.md`](observability.md) for the one-line OTLP exporter wiring and the full span/metric
inventory.
