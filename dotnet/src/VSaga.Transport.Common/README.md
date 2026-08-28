# VSaga.Transport.Common

Shared building blocks for vSaga's broker-based transport adapters (RabbitMQ, MassTransit, Wolverine,
Brighter) — `MiddlewarePipelineTransport`, the shared decorator `VSaga.Chaos`'s fault injection and the
dashboard's topology recording both plug into. An internal dependency of those adapters; not typically
referenced directly by application code.

## Install

```bash
dotnet add package VSaga.Transport.Common
```

## Docs

[docs/transports/index.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/transports/index.md)
for the `IMessageTransport` contract every adapter, including the ones built on this package, implements.

## License

MIT
