import { randomUUID } from 'node:crypto';

import amqp from 'amqplib';
import { RabbitMQContainer, type StartedRabbitMQContainer } from '@testcontainers/rabbitmq';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import {
  CAUSATION_ID_HEADER,
  CORRELATION_ID_HEADER,
  EMPTY_CORRELATION_ID,
  type MessageTransport,
  type ReceivedMessage,
  SOURCE_SERVICE_HEADER,
  newCorrelationId,
  newEnvelope,
} from '@vsaga/protocol';

import { resolveOptions, type RabbitMqTransportOptions } from '../src/options.js';
import { createRabbitMqTransport, parseCorrelationId } from '../src/transport.js';

const CORRELATION = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
const OTHER = '11111111-2222-3333-4444-555555555555';

describe('resolveOptions', () => {
  it('defaults to the same names and prefetch as RabbitMqOptions', () => {
    expect(resolveOptions()).toEqual({
      connectionString: 'amqp://guest:guest@localhost:5672/',
      exchangeName: 'vsaga.saga.events',
      deadLetterExchangeName: 'vsaga.dlx',
      clientProvidedName: 'VSaga',
      prefetchCount: 32,
    });
  });

  it('lets each default be overridden', () => {
    const resolved = resolveOptions({ connectionString: 'amqp://rabbit:5672/', prefetchCount: 1 });
    expect(resolved.connectionString).toBe('amqp://rabbit:5672/');
    expect(resolved.prefetchCount).toBe(1);
    expect(resolved.exchangeName).toBe('vsaga.saga.events');
  });
});

describe('parseCorrelationId', () => {
  // Fallback order pinned against RabbitMqTransport.ParseCorrelationId.
  it('prefers the AMQP property', () => {
    expect(parseCorrelationId(CORRELATION, { [CORRELATION_ID_HEADER]: OTHER })).toBe(CORRELATION);
  });

  it('falls back to the header when the property is absent', () => {
    expect(parseCorrelationId(undefined, { [CORRELATION_ID_HEADER]: CORRELATION })).toBe(
      CORRELATION,
    );
  });

  it('falls back to the header when the property is unparseable', () => {
    expect(parseCorrelationId('not-a-guid', { [CORRELATION_ID_HEADER]: CORRELATION })).toBe(
      CORRELATION,
    );
  });

  it('yields the empty Guid when neither parses', () => {
    expect(parseCorrelationId(undefined, {})).toBe(EMPTY_CORRELATION_ID);
    expect(parseCorrelationId('nonsense', { [CORRELATION_ID_HEADER]: 'also nonsense' })).toBe(
      EMPTY_CORRELATION_ID,
    );
  });
});

/** TaskCompletionSource-alike: lets a test observe both "has it settled yet" and await the eventual value. */
interface Deferred<T> {
  readonly promise: Promise<T>;
  settled: boolean;
  resolve(value: T): void;
}

function deferred<T>(): Deferred<T> {
  let resolveFn!: (value: T) => void;
  const promise = new Promise<T>((resolve) => {
    resolveFn = resolve;
  });
  const self: Deferred<T> = {
    promise,
    settled: false,
    resolve(value) {
      self.settled = true;
      resolveFn(value);
    },
  };
  return self;
}

function uniqueName(prefix: string): string {
  return `${prefix}.${randomUUID()}`;
}

/**
 * Mirrors dotnet/tests/VSaga.Transport.RabbitMQ.Tests/RabbitMqTransportTests.cs against a real
 * broker (Testcontainers, not a mock of amqplib -- the topology declarations, publisher-confirm
 * "mandatory returns unroutable" behaviour, and dead-letter routing are the broker's job, not this
 * adapter's, so a fake channel would only prove the mock agrees with itself), plus TS-specific
 * coverage of dead-letter routing and delivery-count-driven redelivery.
 */
describe('createRabbitMqTransport', () => {
  let container: StartedRabbitMQContainer;
  let connectionString: string;
  const openTransports: MessageTransport[] = [];

  beforeAll(async () => {
    container = await new RabbitMQContainer('rabbitmq:4-management').start();
    connectionString = container.getAmqpUrl();
  }, 120_000);

  afterAll(async () => {
    await container.stop();
  });

  afterEach(async () => {
    await Promise.all(openTransports.splice(0).map((t) => t.close()));
  });

  async function transport(options: RabbitMqTransportOptions = {}): Promise<MessageTransport> {
    const created = await createRabbitMqTransport({ connectionString, ...options });
    openTransports.push(created);
    return created;
  }

  it('delivers a published message to a subscriber with correlation id and type intact', async () => {
    const sender = await transport();
    const receiver = await transport();

    const received = deferred<ReceivedMessage>();
    await receiver.subscribe(
      {
        consumerName: 'TestConsumer',
        messageTypeNames: ['PingMessage'],
        queueNameHint: uniqueName('vsaga.test.ping-queue'),
      },
      async (message) => {
        received.resolve(message);
        await message.ack.ack();
      },
    );

    const correlationId = newCorrelationId();
    await sender.publish(
      'PingMessage',
      Buffer.from(JSON.stringify({ text: 'hello' })),
      newEnvelope(correlationId),
    );

    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
    expect(message.messageTypeName).toBe('PingMessage');
    expect(JSON.parse(message.body.toString('utf8'))).toEqual({ text: 'hello' });
  });

  it('send() delivers directly to a named queue, bypassing the exchange and routing entirely', async () => {
    const sender = await transport();
    const receiver = await transport();
    const queueName = uniqueName('vsaga.test.direct-queue');

    const received = deferred<ReceivedMessage>();
    // No routes/bindings for PingMessage anywhere -- reachable only because send() addresses the
    // queue directly via the default exchange, exactly like SendAsync on the .NET side.
    await receiver.subscribe(
      {
        consumerName: 'TestConsumer2',
        messageTypeNames: ['PingMessage'],
        queueNameHint: queueName,
      },
      async (message) => {
        received.resolve(message);
        await message.ack.ack();
      },
    );

    const correlationId = newCorrelationId();
    await sender.send(
      queueName,
      'PingMessage',
      Buffer.from(JSON.stringify({ text: 'direct' })),
      newEnvelope(correlationId),
    );

    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
  });

  it('publish() to a message type nobody has bound throws an unroutable publish error', async () => {
    const lonely = await transport();

    await expect(
      lonely.publish('NobodyIsListening', Buffer.from('{}'), newEnvelope(newCorrelationId())),
    ).rejects.toMatchObject({
      name: 'MessageTransportPublishError',
      isUnroutable: true,
      messageTypeName: 'NobodyIsListening',
    });
  });

  it('propagates custom x-vsaga- headers and normalizes amqplib Buffer header values to strings', async () => {
    const sender = await transport();
    const receiver = await transport();

    const received = deferred<ReceivedMessage>();
    await receiver.subscribe(
      {
        consumerName: 'TestConsumer3',
        messageTypeNames: ['PingMessage'],
        queueNameHint: uniqueName('vsaga.test.headers-queue'),
      },
      async (message) => {
        received.resolve(message);
        await message.ack.ack();
      },
    );

    const correlationId = newCorrelationId();
    await sender.publish('PingMessage', Buffer.from('{}'), {
      correlationId,
      messageId: 'abc',
      headers: {
        [SOURCE_SERVICE_HEADER]: 'orders-service',
        [CAUSATION_ID_HEADER]: 'causation-123',
      },
    });

    const message = await received.promise;
    // Real amqplib deliveries carry headers as Buffers on the wire; if normalizeHeaders were
    // skipped these would be Buffer instances rather than the strings ReceivedMessage promises.
    expect(message.headers[SOURCE_SERVICE_HEADER]).toBe('orders-service');
    expect(message.headers[CAUSATION_ID_HEADER]).toBe('causation-123');
    expect(typeof message.headers[SOURCE_SERVICE_HEADER]).toBe('string');
  });

  it('binds one consumer queue to every declared message type', async () => {
    const sender = await transport();
    const receiver = await transport();

    const receivedTypes: string[] = [];
    const both = deferred<void>();
    await receiver.subscribe(
      {
        consumerName: 'MultiTypeConsumer',
        messageTypeNames: ['TypeA', 'TypeB'],
        queueNameHint: uniqueName('vsaga.test.multi-queue'),
      },
      async (message) => {
        receivedTypes.push(message.messageTypeName);
        await message.ack.ack();
        if (receivedTypes.length === 2) both.resolve();
      },
    );

    await sender.publish('TypeA', Buffer.from('{}'), newEnvelope(newCorrelationId()));
    await sender.publish('TypeB', Buffer.from('{}'), newEnvelope(newCorrelationId()));

    await both.promise;
    expect(receivedTypes.sort()).toEqual(['TypeA', 'TypeB']);
  });

  it('redelivers a nacked(requeue=true) message to the same queue instead of dead-lettering it', async () => {
    const sender = await transport();
    const receiver = await transport();

    let deliveryCount = 0;
    const secondDelivery = deferred<ReceivedMessage>();
    await receiver.subscribe(
      {
        consumerName: 'RequeueConsumer',
        messageTypeNames: ['RetryMe'],
        queueNameHint: uniqueName('vsaga.test.requeue-queue'),
      },
      async (message) => {
        deliveryCount += 1;
        if (deliveryCount === 1) {
          await message.ack.nack(true);
        } else {
          secondDelivery.resolve(message);
          await message.ack.ack();
        }
      },
    );

    const correlationId = newCorrelationId();
    await sender.publish('RetryMe', Buffer.from('{}'), newEnvelope(correlationId));

    const redelivered = await secondDelivery.promise;
    expect(redelivered.correlationId).toBe(correlationId);
    expect(deliveryCount).toBe(2);
  });

  it('dead-letters a nacked(requeue=false) message into the poison queue with the poison routing key', async () => {
    const sender = await transport();
    const receiver = await transport();
    const queueNameHint = uniqueName('vsaga.test.poison-queue');
    const consumerName = 'PoisonConsumer';

    const handled = deferred<void>();
    await receiver.subscribe(
      { consumerName, messageTypeNames: ['BadMessage'], queueNameHint },
      async (message) => {
        await message.ack.nack(false); // last-resort rejection, e.g. a poison payload
        handled.resolve();
      },
    );

    const correlationId = newCorrelationId();
    await sender.publish(
      'BadMessage',
      Buffer.from(JSON.stringify({ text: 'poison' })),
      newEnvelope(correlationId),
    );
    await handled.promise;

    // Read the poison queue directly with a raw amqplib channel: `${queueNameHint}.poison`,
    // bound to the dead-letter exchange under `${consumerName}.poison` -- RabbitMqTransport's own
    // #declareSubscriptionTopology naming, asserted end-to-end rather than by name inspection.
    const rawConnection = await amqp.connect(connectionString);
    try {
      const rawChannel = await rawConnection.createChannel();
      let poisoned: Awaited<ReturnType<typeof rawChannel.get>> = false;
      for (let attempt = 0; attempt < 20 && poisoned === false; attempt += 1) {
        poisoned = await rawChannel.get(`${queueNameHint}.poison`);
        if (poisoned === false) await new Promise((resolve) => setTimeout(resolve, 50));
      }

      if (poisoned === false) throw new Error('Message never reached the poison queue.');
      expect(JSON.parse(poisoned.content.toString('utf8'))).toEqual({ text: 'poison' });
    } finally {
      await rawConnection.close();
    }
  });

  it('close() releases the connection so a subsequent publish is rejected, not hung', async () => {
    const solo = await createRabbitMqTransport({ connectionString });
    await solo.close();

    await expect(
      solo.publish('AnyType', Buffer.from('{}'), newEnvelope(newCorrelationId())),
    ).rejects.toBeDefined();
  });

  it('close() does not throw when called with active consumer channels and an unused publish channel', async () => {
    const t = await createRabbitMqTransport({ connectionString });
    await t.subscribe(
      {
        consumerName: 'CloseConsumer',
        messageTypeNames: ['Whatever'],
        queueNameHint: uniqueName('vsaga.test.close-queue'),
      },
      async () => {},
    );

    await expect(t.close()).resolves.toBeUndefined();
  });

  /**
   * The publish channel is cached for the life of the transport (unlike .NET, which opens a fresh
   * one per publish -- RabbitMqTransport.PublishInternalAsync's `await using`), so this adapter is
   * the only one of the two with a long-lived channel the broker can kill underneath it: a topology
   * mismatch, a queue deleted out from under a consumer, or the channel teardown that accompanies a
   * dropped connection all close it server-side. Both tests below drive that for real by
   * re-declaring the exchange out-of-band with a conflicting `durable`, which is what a live broker
   * answers with PRECONDITION_FAILED and a channel close.
   */
  describe('publish channel loss', () => {
    /** Replaces the exchange with one whose `durable` conflicts with what the transport asserts. */
    async function redeclareExchange(exchangeName: string, durable: boolean): Promise<void> {
      const connection = await amqp.connect(connectionString);
      try {
        const channel = await connection.createChannel();
        await channel.deleteExchange(exchangeName);
        await channel.assertExchange(exchangeName, 'topic', { durable, autoDelete: false });
      } finally {
        await connection.close();
      }
    }

    it('surfaces a broker-side channel error as a rejected publish instead of killing the process', async () => {
      // amqplib emits 'error' on the Channel itself for a server-initiated close, and its promise
      // API attaches no listener of its own (lib/channel_model.js wires only 'delivery' and
      // 'cancel'). An 'error' event with no listener is an EventEmitter throw, so an unguarded
      // channel takes the whole Node process down on what is a recoverable broker error.
      const exchangeName = uniqueName('vsaga.test.exchange');
      const sender = await transport({ exchangeName });

      // Opens the publish channel and asserts the exchange durable, the state the redeclare below
      // then conflicts with. Unroutable because nothing is bound -- immaterial here.
      await expect(
        sender.publish('Whatever', Buffer.from('{}'), newEnvelope(newCorrelationId())),
      ).rejects.toMatchObject({ isUnroutable: true });

      await redeclareExchange(exchangeName, false);

      await expect(
        sender.publish('Whatever', Buffer.from('{}'), newEnvelope(newCorrelationId())),
      ).rejects.toThrow(/PRECONDITION_FAILED/i);
    });

    it('opens a fresh publish channel after the cached one dies, rather than reusing the dead one', async () => {
      const exchangeName = uniqueName('vsaga.test.exchange');
      const sender = await transport({ exchangeName });

      await expect(
        sender.publish('Whatever', Buffer.from('{}'), newEnvelope(newCorrelationId())),
      ).rejects.toMatchObject({ isUnroutable: true });

      await redeclareExchange(exchangeName, false);
      await expect(
        sender.publish('Whatever', Buffer.from('{}'), newEnvelope(newCorrelationId())),
      ).rejects.toThrow(/PRECONDITION_FAILED/i);

      // Broker back to a state the transport agrees with. Reaching an unroutable return again is
      // the proof: it means the publish got as far as the broker on a working channel. Were the
      // dead channel still cached, this would fail with amqplib's "Channel closed" instead.
      await redeclareExchange(exchangeName, true);

      await expect(
        sender.publish('Whatever', Buffer.from('{}'), newEnvelope(newCorrelationId())),
      ).rejects.toMatchObject({
        name: 'MessageTransportPublishError',
        isUnroutable: true,
      });
    });
  });
});
