# VSaga.Abstractions

Core saga abstractions for vSaga: `SagaState`, `ISagaContext`, message envelopes, and the
transport/persistence interfaces (`IMessageTransport`, `ISagaSnapshotStore<TState>`, `ISagaEventLogStore`,
and the rest) every other vSaga package builds on. Pulled in transitively by `VSaga.Core` and every
transport/persistence adapter — most projects never reference this directly.

## Install

```bash
dotnet add package VSaga.Abstractions
```

## Docs

Full reference: [docs/concepts.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/concepts.md)
(orchestrated vs. choreographed, correlation, compensation, timeouts) and
[docs/README.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/README.md) for the complete
documentation index.

## License

MIT
