# @vsaga/transport-rabbitmq

An `amqplib`-backed vSaga `MessageTransport` — wire-compatible with
`dotnet/src/VSaga.Transport.RabbitMQ`: one durable topic exchange, one durable queue per consumer
with bindings derived from its declared message types, a dead-letter exchange/queue pair per
consumer, and correlation/message-id/type propagation via both AMQP properties and headers.

## Install

```sh
npm install @vsaga/transport-rabbitmq
```

## Usage

```ts
import { createRabbitMqTransport } from '@vsaga/transport-rabbitmq';
import { createParticipant } from '@vsaga/participant';

const transport = await createRabbitMqTransport({
  connectionString: 'amqp://guest:guest@localhost:5672/',
});

const payments = createParticipant({
  serviceName: 'payments',
  queue: 'vsaga.participant.payments',
  transport,
});
// ... register handlers, payments.start() ...
```

## Options

| Option                   | Default                                | Meaning                                                            |
| ------------------------ | -------------------------------------- | ------------------------------------------------------------------ |
| `connectionString`       | `'amqp://guest:guest@localhost:5672/'` | AMQP connection URL.                                               |
| `exchangeName`           | `'vsaga.saga.events'`                  | The single durable topic exchange every publish travels over.      |
| `deadLetterExchangeName` | `'vsaga.dlx'`                          | Dead-letter exchange bound to every consumer's DLQ.                |
| `clientProvidedName`     | `'VSaga'`                              | AMQP client-provided connection name, for broker-side diagnostics. |
| `prefetchCount`          | `32`                                   | Per-channel QoS prefetch, matching the .NET transport's default.   |

## Notes

- `send(destination, ...)` targets the default exchange with `routingKey = destination`, bypassing
  bindings entirely — the same semantics as .NET's `SendAsync`.
- A publish with no matching binding on the configured exchange surfaces as an unroutable-publish
  error via AMQP publisher confirms, not a silent drop.

## License

MIT
