# VSaga.Transport.Brighter

Brighter adapter for vSaga's `IMessageTransport` — run vSaga sagas over an existing
Paramore.Brighter-based messaging setup (its RabbitMQ gateway) instead of the reference
`VSaga.Transport.RabbitMQ` adapter.

## Install

```bash
dotnet add package VSaga.Transport.Brighter
```

## Usage

```csharp
services.AddVSagaBrighter(o => o.ConnectionString = "amqp://guest:guest@localhost:5672/");
```

## Docs

[docs/transports/brighter.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/brighter.md)
— including the header-filtering behavior on receipt and the W3C trace-context exemption from it.

## License

MIT
