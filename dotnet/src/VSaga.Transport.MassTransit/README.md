# VSaga.Transport.MassTransit

MassTransit adapter for vSaga's `IMessageTransport` — run vSaga sagas over an existing MassTransit
8.x-based messaging setup instead of the reference `VSaga.Transport.RabbitMQ` adapter. Deliberately
pinned below MassTransit 9.0.0 (Apache-2.0; v9 is commercially licensed).

## Install

```bash
dotnet add package VSaga.Transport.MassTransit
```

## Usage

```csharp
services.AddVSagaMassTransit(o => o.ConnectionString = "amqp://guest:guest@localhost:5672/");
```

## Docs

[docs/transports/masstransit.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/masstransit.md).

## License

MIT
