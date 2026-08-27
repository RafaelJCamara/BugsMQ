# @vsaga/participant

Framework-agnostic vSaga participant runtime for Node: register handlers for the message types you
consume, and this takes care of dispatch, duplicate-delivery dedup, ack/nack, and replying with the
correct causation link.

Participants are not sagas — they hold no state, run no timeouts, and never talk to the engine
directly, exactly like the .NET sample participants that reference only `VSaga.Abstractions`.
That's what makes cross-runtime participation possible at all.

## Install

```sh
npm install @vsaga/participant @vsaga/protocol
```

You'll also need a transport — `@vsaga/transport-http` or `@vsaga/transport-rabbitmq`.

## Usage

```ts
import { createParticipant } from '@vsaga/participant';
import { message } from '@vsaga/protocol';
import { createRabbitMqTransport } from '@vsaga/transport-rabbitmq';

interface ChargeCardBody {
  CorrelationId: string;
  OrderId: string;
  Amount: number;
}
const ChargeCard = message<ChargeCardBody>('ChargeCard');

const CardCharged = message<{ CorrelationId: string; OrderId: string }>('CardCharged');

const transport = await createRabbitMqTransport({ connectionString: 'amqp://localhost' });

const payments = createParticipant({
  serviceName: 'payments',
  queue: 'vsaga.participant.payments',
  transport,
});

payments.on(ChargeCard, async (body, ctx) => {
  // ... charge the card ...
  await ctx.reply(CardCharged, { CorrelationId: body.CorrelationId, OrderId: body.OrderId });
});

await payments.start();
```

## Dispatch semantics

Ported 1:1 from the .NET reference participant:

- Unknown message type → ack and drop (someone else's message on a shared binding).
- Duplicate message id → ack and skip, via the pluggable `IdempotencyStore`.
- Handler resolves → ack.
- Handler throws → `nack(requeue: false)` — straight to the dead-letter queue, not a hot redelivery
  loop.
- A handler may legitimately reply zero times (e.g. a compensating command that must be an
  idempotent no-op).

Inside a handler, `HandlerContext` gives you `reply` (causally linked to the inbound message),
`publish` (a self-initiated event, no causation link), and `send` (straight to a named queue,
bypassing bindings) — plus manual `ack`/`nack` when `autoAck: false` is set for handlers that must
only acknowledge after an external commit.

## Topology reporting

Pass a `topology: TopologyReporter` (see `httpTopologyReporter` in this package, or
`@vsaga/transport-http`'s equivalent) so this service resolves to a named node on the dashboard's
Saga Map instead of an `Unresolved` one. Registration is best-effort — a cold dashboard never stops
a participant from starting.

## License

MIT
