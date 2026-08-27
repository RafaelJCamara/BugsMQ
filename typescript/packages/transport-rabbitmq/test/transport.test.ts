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
});
