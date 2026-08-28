import { describe, expect, it } from 'vitest';
import {
  CAUSATION_ID_HEADER,
  SOURCE_SERVICE_HEADER,
  TRACE_PARENT_HEADER,
  TRACE_STATE_HEADER,
  decodeBody,
  message,
  newCorrelationId,
  newMessageId,
} from '@vsaga/protocol';

import { InMemoryIdempotencyStore } from '../src/idempotency.js';
import { type Handler, createParticipant } from '../src/participant.js';
import { FakeTransport } from './test-transport.js';

interface ShipOrderBody {
  CorrelationId: string;
  OrderId: string;
}
interface OrderShippedBody {
  CorrelationId: string;
  OrderId: string;
  TrackingNumber: string;
}

const ShipOrder = message<ShipOrderBody>('ShipOrder');
const OrderShipped = message<OrderShippedBody>('OrderShipped');

function shipOrder(correlationId = newCorrelationId(), messageId = newMessageId()) {
  return {
    messageTypeName: 'ShipOrder',
    correlationId,
    messageId,
    body: { CorrelationId: correlationId, OrderId: 'ORD-1' } satisfies ShipOrderBody,
  };
}

async function started(transport: FakeTransport, handler: Handler<ShipOrderBody>) {
  const participant = createParticipant({
    serviceName: 'ShippingService',
    queue: 'vsaga.participant.shipping',
    transport,
  });
  participant.on(ShipOrder, handler);
  await participant.start();
  return participant;
}

describe('subscription', () => {
  it('declares the subscription from the registered types', async () => {
    const transport = new FakeTransport();
    await started(transport, () => {});

    expect(transport.subscription).toEqual({
      consumerName: 'ShippingService',
      messageTypeNames: ['ShipOrder'],
      queueNameHint: 'vsaga.participant.shipping',
    });
  });

  it('refuses to start with no handlers, which would bind nothing', async () => {
    const participant = createParticipant({
      serviceName: 'ShippingService',
      queue: 'q',
      transport: new FakeTransport(),
    });

    await expect(participant.start()).rejects.toThrow(/no handlers/);
  });

  it('refuses a duplicate handler registration', async () => {
    const participant = createParticipant({
      serviceName: 'S',
      queue: 'q',
      transport: new FakeTransport(),
    });
    participant.on(ShipOrder, () => {});

    expect(() => participant.on(ShipOrder, () => {})).toThrow(/already has a handler/);
  });

  it('refuses registration after start, when bindings are already declared', async () => {
    const transport = new FakeTransport();
    const participant = await started(transport, () => {});

    expect(() => participant.on(OrderShipped, () => {})).toThrow(/after .* has started/);
  });
});

describe('dispatch', () => {
  it('acks and drops an unhandled message type', async () => {
    const transport = new FakeTransport();
    await started(transport, () => {
      throw new Error('must not run');
    });

    await transport.deliver({ ...shipOrder(), messageTypeName: 'SomeoneElsesMessage' });

    expect(transport.acked).toHaveLength(1);
    expect(transport.nacked).toHaveLength(0);
  });

  it('acks after a handler resolves', async () => {
    const transport = new FakeTransport();
    await started(transport, () => {});

    await transport.deliver(shipOrder());

    expect(transport.acked).toHaveLength(1);
    expect(transport.nacked).toHaveLength(0);
  });

  it('nacks without requeue when a handler throws, sending it to the DLQ', async () => {
    const transport = new FakeTransport();
    await started(transport, () => {
      throw new Error('carrier exploded');
    });

    await transport.deliver(shipOrder());

    expect(transport.nacked).toEqual([{ messageId: expect.any(String), requeue: false }]);
    expect(transport.acked).toHaveLength(0);
  });

  it('decodes the PascalCase body', async () => {
    const transport = new FakeTransport();
    let seen: ShipOrderBody | undefined;
    await started(transport, (body) => {
      seen = body;
    });

    await transport.deliver(shipOrder());

    expect(seen?.OrderId).toBe('ORD-1');
  });

  it('treats a validation failure as a handler failure', async () => {
    const transport = new FakeTransport();
    const participant = createParticipant({
      serviceName: 'ShippingService',
      queue: 'q',
      transport,
    });

    participant.on(ShipOrder, () => {}, {
      validate: {
        parse: (input) => {
          const body = input as ShipOrderBody;
          if (typeof body.OrderId !== 'string') throw new TypeError('OrderId must be a string');
          return body;
        },
      },
    });
    await participant.start();

    await transport.deliver({ ...shipOrder(), body: { CorrelationId: 'x' } as never });

    expect(transport.nacked).toHaveLength(1);
    expect(transport.acked).toHaveLength(0);
  });

  it('lets a handler reply zero times', async () => {
    const transport = new FakeTransport();
    await started(transport, () => {});

    await transport.deliver(shipOrder());

    expect(transport.published).toHaveLength(0);
    expect(transport.acked).toHaveLength(1);
  });
});

describe('dedupe', () => {
  it('skips a repeat delivery of the same message id but still acks it', async () => {
    const transport = new FakeTransport();
    let runs = 0;
    await started(transport, () => {
      runs++;
    });

    const delivery = shipOrder();
    await transport.deliver(delivery);
    await transport.deliver(delivery);

    expect(runs).toBe(1);
    expect(transport.acked).toHaveLength(2);
  });

  it('does not confuse two distinct messages on one correlation', async () => {
    const transport = new FakeTransport();
    let runs = 0;
    await started(transport, () => {
      runs++;
    });

    const correlationId = newCorrelationId();
    await transport.deliver(shipOrder(correlationId, newMessageId()));
    await transport.deliver(shipOrder(correlationId, newMessageId()));

    expect(runs).toBe(2);
  });
});

describe('InMemoryIdempotencyStore', () => {
  it('claims once, then refuses', () => {
    const store = new InMemoryIdempotencyStore();
    expect(store.tryClaim('a')).toBe(true);
    expect(store.tryClaim('a')).toBe(false);
  });

  it('evicts oldest-first at the bound so uptime cannot grow it unbounded', () => {
    const store = new InMemoryIdempotencyStore(3);
    for (const id of ['a', 'b', 'c', 'd']) store.tryClaim(id);

    expect(store.size).toBe(3);
    // 'a' was evicted, so it is claimable again; 'd' is still tracked.
    expect(store.tryClaim('a')).toBe(true);
    expect(store.tryClaim('d')).toBe(false);
  });
});

describe('reply', () => {
  it('stamps source service, causation, and a fresh message id', async () => {
    const transport = new FakeTransport();
    await started(transport, (body, ctx) =>
      ctx.reply(OrderShipped, {
        CorrelationId: ctx.correlationId,
        OrderId: body.OrderId,
        TrackingNumber: 'TRK-1',
      }),
    );

    const delivery = shipOrder();
    await transport.deliver(delivery);

    const published = transport.published[0]!;
    expect(published.messageTypeName).toBe('OrderShipped');
    expect(published.envelope.correlationId).toBe(delivery.correlationId);
    expect(published.envelope.headers[SOURCE_SERVICE_HEADER]).toBe('ShippingService');
    expect(published.envelope.headers[CAUSATION_ID_HEADER]).toBe(delivery.messageId);
    expect(published.envelope.messageId).not.toBe(delivery.messageId);
  });

  it('encodes the reply body as PascalCase JSON', async () => {
    const transport = new FakeTransport();
    await started(transport, (body, ctx) =>
      ctx.reply(OrderShipped, {
        CorrelationId: ctx.correlationId,
        OrderId: body.OrderId,
        TrackingNumber: 'TRK-1',
      }),
    );

    await transport.deliver(shipOrder());

    const decoded = decodeBody<OrderShippedBody>(transport.published[0]!.body);
    expect(decoded.TrackingNumber).toBe('TRK-1');
    expect(decoded).not.toHaveProperty('trackingNumber');
  });

  it('threads traceparent/tracestate from the inbound message onto the reply (production readiness §8.17)', async () => {
    const transport = new FakeTransport();
    await started(transport, (body, ctx) =>
      ctx.reply(OrderShipped, {
        CorrelationId: ctx.correlationId,
        OrderId: body.OrderId,
        TrackingNumber: 'TRK-1',
      }),
    );

    const traceParent = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
    const traceState = 'vendor1=value1,vendor2=value2';
    await transport.deliver({
      ...shipOrder(),
      headers: { [TRACE_PARENT_HEADER]: traceParent, [TRACE_STATE_HEADER]: traceState },
    });

    const published = transport.published[0]!;
    expect(published.envelope.headers[TRACE_PARENT_HEADER]).toBe(traceParent);
    expect(published.envelope.headers[TRACE_STATE_HEADER]).toBe(traceState);
  });

  it('omits traceparent/tracestate from the reply when the inbound message carried none', async () => {
    const transport = new FakeTransport();
    await started(transport, (body, ctx) =>
      ctx.reply(OrderShipped, {
        CorrelationId: ctx.correlationId,
        OrderId: body.OrderId,
        TrackingNumber: 'TRK-1',
      }),
    );

    await transport.deliver(shipOrder());

    const published = transport.published[0]!;
    expect(published.envelope.headers).not.toHaveProperty(TRACE_PARENT_HEADER);
    expect(published.envelope.headers).not.toHaveProperty(TRACE_STATE_HEADER);
  });

  it('publish() omits the causation link', async () => {
    const transport = new FakeTransport();
    await started(transport, (body, ctx) =>
      ctx.publish(OrderShipped, {
        CorrelationId: ctx.correlationId,
        OrderId: body.OrderId,
        TrackingNumber: 'TRK-1',
      }),
    );

    await transport.deliver(shipOrder());

    expect(transport.published[0]!.envelope.headers).not.toHaveProperty(CAUSATION_ID_HEADER);
  });

  it('send() targets a named destination', async () => {
    const transport = new FakeTransport();
    await started(transport, (body, ctx) =>
      ctx.send('vsaga.participant.other', OrderShipped, {
        CorrelationId: ctx.correlationId,
        OrderId: body.OrderId,
        TrackingNumber: 'TRK-1',
      }),
    );

    await transport.deliver(shipOrder());

    expect(transport.published[0]!.destination).toBe('vsaga.participant.other');
  });

  it('replies on the inbound correlation id, not one derived from the body', async () => {
    const transport = new FakeTransport();
    await started(transport, (_body, ctx) =>
      ctx.reply(OrderShipped, {
        CorrelationId: ctx.correlationId,
        OrderId: 'ORD-1',
        TrackingNumber: 'TRK-1',
      }),
    );

    // Body disagrees with the transport-level id. The engine reads the transport-level one.
    const delivery = shipOrder();
    await transport.deliver({
      ...delivery,
      body: { CorrelationId: newCorrelationId(), OrderId: 'ORD-1' },
    });

    expect(transport.published[0]!.envelope.correlationId).toBe(delivery.correlationId);
  });
});

describe('manual ack mode', () => {
  it('leaves ack to the handler', async () => {
    const transport = new FakeTransport();
    const participant = createParticipant({
      serviceName: 'ShippingService',
      queue: 'q',
      transport,
      autoAck: false,
    });
    participant.on(ShipOrder, (_body, ctx) => ctx.ack());
    await participant.start();

    await transport.deliver(shipOrder());

    expect(transport.acked).toHaveLength(1);
  });

  it('does not auto-nack a throwing handler', async () => {
    const transport = new FakeTransport();
    const participant = createParticipant({
      serviceName: 'S',
      queue: 'q',
      transport,
      autoAck: false,
    });
    participant.on(ShipOrder, () => {
      throw new Error('boom');
    });
    await participant.start();

    await transport.deliver(shipOrder());

    expect(transport.nacked).toHaveLength(0);
  });
});

describe('topology', () => {
  it('reports one registration per handled type before subscribing', async () => {
    const transport = new FakeTransport();
    const reported: unknown[] = [];

    const participant = createParticipant({
      serviceName: 'ShippingService',
      queue: 'vsaga.participant.shipping',
      transport,
      topology: {
        report: (registrations) => {
          reported.push(...registrations);
          return Promise.resolve();
        },
      },
    });
    participant.on(ShipOrder, () => {});
    participant.on(OrderShipped, () => {});
    await participant.start();

    expect(reported).toEqual([
      {
        serviceName: 'ShippingService',
        messageType: 'ShipOrder',
        queueName: 'vsaga.participant.shipping',
      },
      {
        serviceName: 'ShippingService',
        messageType: 'OrderShipped',
        queueName: 'vsaga.participant.shipping',
      },
    ]);
  });

  it('still starts when topology reporting fails', async () => {
    const transport = new FakeTransport();
    const participant = createParticipant({
      serviceName: 'ShippingService',
      queue: 'q',
      transport,
      topology: { report: () => Promise.reject(new Error('dashboard is cold')) },
    });
    participant.on(ShipOrder, () => {});

    await expect(participant.start()).resolves.toBeUndefined();
    expect(participant.running).toBe(true);
  });
});

describe('lifecycle', () => {
  it('start is idempotent and stop closes the subscription', async () => {
    const transport = new FakeTransport();
    const participant = await started(transport, () => {});

    await participant.start();
    expect(participant.running).toBe(true);

    await participant.stop();
    expect(participant.running).toBe(false);

    await participant.stop();
    expect(participant.running).toBe(false);
  });
});
