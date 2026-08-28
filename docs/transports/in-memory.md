# Transport adapter: in-memory

`VSaga.Transport.InMemory` (`AddVSagaInMemoryTransport()`) is a single-process `IMessageTransport` with
no broker, no network, and no serialization — a publish dispatches synchronously and recursively to
every matching in-process subscriber. It underlies `SagaTestHarness` (see
[`../testing.md`](../testing.md)) and is convenient for local development without standing up
Postgres/RabbitMQ, but it is **not for production use**: state does not survive a process restart, and
nothing about it models the failure modes (lost/duplicated/delayed delivery, partition) a real
transport has to handle.

Because dispatch is synchronous and recursive, a chain of self-published messages resolves entirely
within the original `PublishAsync`/`WhenAsync` call — this is what lets `SagaTestHarness.WhenAsync`
return only once a saga has fully processed a message, with no polling. It is also the reason a small
number of race conditions in this repo's sub-saga composition were only reproducible under this
transport's synchronous dispatch (a child notifying its parent from the very step that started it can
race ahead of the parent's own not-yet-persisted transition) — see
[`../concepts.md`](../concepts.md#sub-saga-composition) and
[`../history/sub-saga-completion-notification.md`](../history/sub-saga-completion-notification.md).
Every real transport decouples a subscriber's dispatch from the publisher's own call stack, so this
narrow hazard is specific to the in-memory adapter's synchronous model, not a property of the engine
generally.

```csharp
services.AddVSagaInMemoryTransport();
services.AddVSagaInMemoryPersistence();
```

No options to configure.
