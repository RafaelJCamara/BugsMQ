import amqp, {
  type ChannelModel,
  type ConfirmChannel,
  type Channel,
  type ConsumeMessage,
} from 'amqplib';
import {
  CORRELATION_ID_HEADER,
  EMPTY_CORRELATION_ID,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  type MessageEnvelope,
  type MessageTransport,
  MessageTransportPublishError,
  type ReceivedMessage,
  type Subscription,
  type TransportSubscription,
  assertHeadersSafe,
  buildHeaders,
  isDashedGuid,
  normalizeHeaders,
  toRoutingKey,
} from '@vsaga/protocol';

import {
  type RabbitMqTransportOptions,
  type ResolvedRabbitMqOptions,
  resolveOptions,
} from './options.js';

/**
 * amqplib-backed MessageTransport, wire-compatible with
 * dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs: one durable topic exchange, one durable
 * queue per consumer with bindings derived from its declared message types, a dead-letter
 * exchange/queue pair per consumer, and correlation/message-id/type propagation via both AMQP
 * properties and headers.
 */
export async function createRabbitMqTransport(
  options: RabbitMqTransportOptions = {},
): Promise<MessageTransport> {
  const resolved = resolveOptions(options);
  const connection = await amqp.connect(resolved.connectionString, {
    clientProperties: { connection_name: resolved.clientProvidedName },
  });

  return new RabbitMqTransport(connection, resolved);
}

class RabbitMqTransport implements MessageTransport {
  readonly #connection: ChannelModel;
  readonly #options: ResolvedRabbitMqOptions;
  readonly #consumerChannels: Channel[] = [];
  readonly #pendingPublishes = new Map<string, (error: MessageTransportPublishError) => void>();

  #publishChannel: ConfirmChannel | undefined;

  constructor(connection: ChannelModel, options: ResolvedRabbitMqOptions) {
    this.#connection = connection;
    this.#options = options;
  }

  publish(messageTypeName: string, body: Buffer, envelope: MessageEnvelope): Promise<void> {
    return this.#publishInternal(messageTypeName, body, envelope, undefined);
  }

  send(
    destination: string,
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
  ): Promise<void> {
    return this.#publishInternal(messageTypeName, body, envelope, destination);
  }

  async #publishInternal(
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    destinationQueue: string | undefined,
  ): Promise<void> {
    assertHeadersSafe(envelope.headers);

    const channel = await this.#getPublishChannel();
    const headers = buildHeaders(envelope, messageTypeName);

    // A direct address is either configured or it isn't: SendAsync targets the default exchange
    // with routingKey = the queue name, bypassing bindings entirely, exactly as .NET does.
    const exchange = destinationQueue !== undefined ? '' : this.#options.exchangeName;
    const routingKey = destinationQueue ?? toRoutingKey(messageTypeName);

    if (destinationQueue === undefined) await this.#ensureExchange(channel);

    await new Promise<void>((resolve, reject) => {
      // Keyed by messageId so the 'return' listener (below) can reject *this* publish specifically
      // rather than every publish in flight on the shared channel.
      this.#pendingPublishes.set(envelope.messageId, reject);

      channel.publish(
        exchange,
        routingKey,
        body,
        {
          correlationId: envelope.correlationId,
          messageId: envelope.messageId,
          contentType: 'application/json',
          deliveryMode: 2,
          // mandatory + publisher confirms is what turns a wrong routing key from a silent
          // disappearance into an error. .NET gets this via tracked confirmations; here the
          // callback surfaces a nack and the 'return' listener (below) surfaces an unroutable
          // publish, and both reject this promise.
          mandatory: true,
          headers,
        },
        (error) => {
          // Already settled by the 'return' listener: an unroutable mandatory publish is still
          // confirmed after being returned, so this callback still fires and must not resolve a
          // promise that's already been rejected.
          if (!this.#pendingPublishes.delete(envelope.messageId)) return;

          if (error) {
            reject(
              new MessageTransportPublishError(messageTypeName, envelope.correlationId, false, {
                cause: error,
              }),
            );
          } else {
            resolve();
          }
        },
      );
    });
  }

  async subscribe(
    subscription: TransportSubscription,
    handler: (message: ReceivedMessage) => Promise<void>,
  ): Promise<Subscription> {
    const channel = await this.#connection.createChannel();
    this.#consumerChannels.push(channel);

    await channel.prefetch(this.#options.prefetchCount, false);
    await this.#ensureExchange(channel);
    await this.#declareSubscriptionTopology(channel, subscription);

    const { consumerTag } = await channel.consume(
      subscription.queueNameHint,
      (message) => {
        if (message === null) return; // consumer cancelled by the broker
        void this.#dispatch(channel, handler, message);
      },
      { noAck: false },
    );

    return {
      close: async () => {
        try {
          await channel.cancel(consumerTag);
          await channel.close();
        } finally {
          const index = this.#consumerChannels.indexOf(channel);
          if (index >= 0) this.#consumerChannels.splice(index, 1);
        }
      },
    };
  }

  /**
   * One durable queue per consumer bound to the exchange for each declared message type, plus a
   * dead-letter exchange/queue pair for messages that exhaust redelivery.
   *
   * Every argument here must match .NET's declaration byte-for-byte. Re-declaring an existing queue
   * with different arguments fails with PRECONDITION_FAILED, and amqplib kills the *channel* on
   * that, which surfaces as a confusing "channel closed" rather than a clear error.
   */
  async #declareSubscriptionTopology(
    channel: Channel,
    subscription: TransportSubscription,
  ): Promise<void> {
    const poisonRoutingKey = `${subscription.consumerName}.poison`;
    const poisonQueueName = `${subscription.queueNameHint}.poison`;

    await channel.assertExchange(this.#options.deadLetterExchangeName, 'topic', {
      durable: true,
      autoDelete: false,
    });
    await channel.assertQueue(poisonQueueName, {
      durable: true,
      exclusive: false,
      autoDelete: false,
    });
    await channel.bindQueue(
      poisonQueueName,
      this.#options.deadLetterExchangeName,
      poisonRoutingKey,
    );

    await channel.assertQueue(subscription.queueNameHint, {
      durable: true,
      exclusive: false,
      autoDelete: false,
      arguments: {
        'x-dead-letter-exchange': this.#options.deadLetterExchangeName,
        'x-dead-letter-routing-key': poisonRoutingKey,
      },
    });

    for (const messageTypeName of subscription.messageTypeNames) {
      await channel.bindQueue(
        subscription.queueNameHint,
        this.#options.exchangeName,
        toRoutingKey(messageTypeName),
      );
    }
  }

  async #dispatch(
    channel: Channel,
    handler: (message: ReceivedMessage) => Promise<void>,
    message: ConsumeMessage,
  ): Promise<void> {
    const headers = normalizeHeaders(message.properties.headers as Record<string, unknown>);

    // Fallback order mirrors DispatchReceivedAsync exactly. Note the message-type fallback to the
    // routing key yields e.g. `order-shipped`, which never matches a CLR short type name -- it is a
    // bug detector, not a working path.
    const messageTypeName = headers[MESSAGE_TYPE_HEADER] ?? message.fields.routingKey;
    const messageId =
      (message.properties.messageId as string | undefined) ??
      headers[MESSAGE_ID_HEADER] ??
      String(message.fields.deliveryTag);

    const received: ReceivedMessage = {
      messageTypeName,
      correlationId: parseCorrelationId(
        message.properties.correlationId as string | undefined,
        headers,
      ),
      messageId,
      body: message.content,
      headers,
      ack: {
        ack: () => {
          channel.ack(message);
          return Promise.resolve();
        },
        nack: (requeue: boolean) => {
          channel.nack(message, false, requeue);
          return Promise.resolve();
        },
      },
    };

    try {
      await handler(received);
    } catch {
      // The participant runtime acks/nacks itself; this only fires for an unexpected failure in
      // dispatch itself. Same last-resort nack as .NET's.
      try {
        channel.nack(message, false, false);
      } catch {
        // Channel already gone; nothing useful left to do.
      }
    }
  }

  async #getPublishChannel(): Promise<ConfirmChannel> {
    if (this.#publishChannel) return this.#publishChannel;

    const channel = await this.#connection.createConfirmChannel();

    // Without this, a `mandatory` publish to an unbound routing key is returned by the broker and
    // silently dropped -- the confirm callback still reports success.
    channel.on('return', (returned) => {
      const returnedHeaders = normalizeHeaders(
        returned.properties.headers as Record<string, unknown>,
      );
      const messageTypeName =
        returnedHeaders[MESSAGE_TYPE_HEADER] ?? returned.fields.routingKey ?? 'unknown';
      const correlationId =
        (returned.properties.correlationId as string | undefined) ?? EMPTY_CORRELATION_ID;
      const error = new MessageTransportPublishError(messageTypeName, correlationId, true);

      // Reject the specific pending publish this return belongs to. If none is pending (the
      // messageId is missing, or its confirm callback already ran), there is no promise left to
      // reject onto, so surface it as a channel error instead of leaving it silent.
      const messageId = returned.properties.messageId as string | undefined;
      const reject = messageId ? this.#pendingPublishes.get(messageId) : undefined;

      if (reject) {
        this.#pendingPublishes.delete(messageId as string);
        reject(error);
      } else {
        channel.emit('error', error);
      }
    });

    channel.on('close', () => {
      if (this.#publishChannel === channel) this.#publishChannel = undefined;
    });

    this.#publishChannel = channel;
    return channel;
  }

  #ensureExchange(channel: Channel): Promise<unknown> {
    return channel.assertExchange(this.#options.exchangeName, 'topic', {
      durable: true,
      autoDelete: false,
    });
  }

  async close(): Promise<void> {
    for (const channel of [...this.#consumerChannels]) {
      try {
        await channel.close();
      } catch {
        // Already closed.
      }
    }
    this.#consumerChannels.length = 0;

    if (this.#publishChannel) {
      try {
        await this.#publishChannel.close();
      } catch {
        // Already closed.
      }
      this.#publishChannel = undefined;
    }

    await this.#connection.close();
  }
}

/**
 * Property first, then header, then Guid.Empty -- ParseCorrelationId's exact order.
 *
 * EMPTY_CORRELATION_ID reaching the orchestrator is a silent failure: it fails CanInitiate and gets
 * logged as an UnexpectedEvent, with no exception and no nack, so the saga sits until its timeout
 * and reads as a hung participant.
 */
export function parseCorrelationId(
  property: string | undefined,
  headers: Record<string, string>,
): string {
  if (property && isDashedGuid(property)) return property;

  const fromHeader = headers[CORRELATION_ID_HEADER];
  return fromHeader && isDashedGuid(fromHeader) ? fromHeader : EMPTY_CORRELATION_ID;
}
