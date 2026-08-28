# VSaga.Transport.Wolverine

Wolverine adapter for vSaga's `IMessageTransport` — run vSaga sagas over an existing
WolverineFx.RabbitMQ-based messaging setup instead of the reference `VSaga.Transport.RabbitMQ` adapter.
Wire-compatible with the RabbitMQ-family adapters: same topic exchange, same routing-key convention.

## Install

```bash
dotnet add package VSaga.Transport.Wolverine
```

## Usage

```csharp
services.AddVSagaWolverine(o => o.ConnectionString = "amqp://guest:guest@localhost:5672/");
```

## Docs

[docs/transports/wolverine.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/wolverine.md)
covers what's different from the reference RabbitMQ adapter — notably, no unroutable-publish detection
(Wolverine's underlying gateway has no equivalent as of the pinned version).

## License

MIT
