# Transport adapter: Brighter

`VSaga.Transport.Brighter`, built on `Paramore.Brighter` + `Paramore.Brighter.MessagingGateway.RMQ.Async`
10.7.0, implements `IMessageTransport` directly on Brighter's transport-level primitives
(`RmqMessageProducer.SendAsync` to publish, `RmqMessageConsumer` to receive/ack/reject) — never
Brighter's `CommandProcessor` dispatch pipeline, its Outbox/Inbox, or its request-handler routing.
Constructed directly as plain singletons rather than through `services.AddBrighter(...)`, since that
helper wires up the dispatch/outbox stack this adapter must not depend on. Full build history and
live-verification detail: [`../history/transport-adapter-brighter.md`](../history/transport-adapter-brighter.md).

- **No default exchange for a direct send.** Brighter's `RmqMessageProducer` is bound to exactly one
  exchange for its whole lifetime and always publishes using the message's `Topic` as the routing key —
  there's no "default/nameless exchange" concept anywhere in the package. `SendAsync` instead binds the
  target queue's own name as an *extra* routing key on the shared topic exchange, so a direct send
  publishes with that routing key — mechanically different route, functionally identical outcome to
  RabbitMQ's own adapter.
- **One queue, many routing keys, needs a lower-level primitive.** The higher-level `Subscription`/
  `IAmAChannelFactory` API exposes only a single `RoutingKey` per subscription — `RmqMessageConsumer`'s
  own constructor (which takes a `RoutingKeys` collection) is used directly instead.
- **Pull-based consumption needs its own pump.** `IAmAMessageConsumerAsync.ReceiveAsync(timeout)` is
  poll-based, unlike RabbitMQ.Client's push-based consumer — `SubscribeAsync` runs its own background
  loop, playing the role Brighter's own Service Activator pump would (deliberately never brought in).
- **Topology declares lazily by default — forced eager.** `RmqMessageConsumer.EnsureChannelAsync`
  declares the queue/bindings inside `ReceiveAsync` itself, not the constructor, so a message published
  before a fresh consumer's first receive is silently dropped. `SubscribeAsync` forces topology to
  exist before returning (the contract `IMessageTransport.SubscribeAsync` requires) with a 50ms
  warm-up receive before starting the real consume loop.
- **Unroutable-publish detection: absent, confirmed by binary inspection and live testing.**
  `RmqMessageProducer` never sets AMQP's `mandatory` flag and exposes no way to request it — publishing
  to a routing key nobody has ever bound a queue to still yields a broker-side confirm `Success = true`.
  `BrighterTransport.SendWithConfirmationAsync` still wires up the confirmation event and throws
  `MessageTransportPublishException` on an explicit `Success = false` (a genuine broker-side nack, e.g.
  a queue at its length limit) — a strictly smaller net than RabbitMQ's mandatory-plus-confirms
  combination catches. The test
  `Publish_ToUnboundRoutingKey_DoesNotThrow_NoMandatoryReturnSupportInBrighterRmqGateway` documents this
  verified behaviour directly.
- **Header filtering on receipt.** `ReceivedMessage.Headers` is filtered to the `x-vsaga-` prefix on
  the way in, rather than passed through unfiltered — Brighter's own `Bag` carries CloudEvents-flavoured
  echoes of core fields on receipt (`CorrelationId`, `Topic`, `HandledCount`, `cloudEvents_id`, ...)
  that would otherwise leak forward as bogus outbound headers on redelivery.
- **A known intermittent condition, not a defect.** The low-traffic sub-saga queues occasionally logged
  `ChannelFailureException`/`precondition_failed: unknown delivery tag` following RabbitMQ.Client's
  automatic connection recovery — a documented client limitation (a message delivered-but-unacked
  immediately before a recovery can't be acked against the recovered channel's restarted delivery-tag
  numbering), not unique to Brighter's gateway. The existing catch-log-retry loop recovers automatically
  and no message was lost during live verification.

Options: [`../configuration.md#brighteroptions-vsagatransportbrighter`](../configuration.md#brighteroptions-vsagatransportbrighter).
Compose overlay: `docker-compose.brighter.yml` (uses the `!override` YAML merge tag on `ports` — Compose's
default list-merge concatenates `ports` arrays across `-f` files instead of replacing them).
