import { describe, expect, it } from 'vitest';

import {
  CAUSATION_ID_HEADER,
  CORRELATION_ID_HEADER,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  SOURCE_SERVICE_HEADER,
  assertHeadersSafe,
  buildHeaders,
  decodeBody,
  encodeBody,
  envelopeFrom,
  isDashedGuid,
  isUndashedGuid,
  message,
  newCorrelationId,
  newEnvelope,
  newMessageId,
  normalizeHeaders,
  toRoutingKey,
} from './index.js';

const CORRELATION = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';

describe('toRoutingKey', () => {
  // Golden cases pinned against DefaultRoutingKeyConvention.ToKebabCase. The last three are the
  // ones where a naive lodash.kebabCase would silently produce a key nothing is bound to.
  it.each([
    ['ReserveInventory', 'reserve-inventory'],
    ['OrderShipped', 'order-shipped'],
    ['ShipOrder', 'ship-order'],
    ['InventoryReservationFailed', 'inventory-reservation-failed'],
    ['A', 'a'],
    ['HTTPOrder', 'h-t-t-p-order'],
    ['OrderID', 'order-i-d'],
    ['Order2Ship', 'order2-ship'],
  ])('maps %s -> %s', (input, expected) => {
    expect(toRoutingKey(input)).toBe(expected);
  });

  it('does not insert a leading dash', () => {
    expect(toRoutingKey('Order').startsWith('-')).toBe(false);
  });
});

describe('guid formats', () => {
  it('mints correlation ids dashed and message ids undashed', () => {
    const correlationId = newCorrelationId();
    const messageId = newMessageId();

    expect(isDashedGuid(correlationId)).toBe(true);
    expect(isUndashedGuid(messageId)).toBe(true);
    expect(messageId).toHaveLength(32);
    expect(messageId).not.toContain('-');
  });

  it('rejects an undashed correlation id', () => {
    expect(() => newEnvelope(newMessageId())).toThrow(TypeError);
  });
});

describe('envelopeFrom', () => {
  it('stamps source service and causation, and mints a fresh message id', () => {
    const inboundMessageId = newMessageId();
    const envelope = envelopeFrom('ShippingService', CORRELATION, inboundMessageId);

    expect(envelope.correlationId).toBe(CORRELATION);
    expect(envelope.headers[SOURCE_SERVICE_HEADER]).toBe('ShippingService');
    expect(envelope.headers[CAUSATION_ID_HEADER]).toBe(inboundMessageId);

    // Reusing the inbound id is the silent-failure case: the orchestrator dedupes on
    // (SagaType, correlationId, messageId) and would drop the reply.
    expect(envelope.messageId).not.toBe(inboundMessageId);
    expect(isUndashedGuid(envelope.messageId)).toBe(true);
  });

  it('omits the causation header when there is no inbound message', () => {
    const envelope = envelopeFrom('OrderApi', CORRELATION);
    expect(envelope.headers).not.toHaveProperty(CAUSATION_ID_HEADER);
  });

  it('never propagates engine-owned headers onto a reply', () => {
    const envelope = envelopeFrom('ShippingService', CORRELATION, newMessageId(), {
      'x-vsaga-delivery-attempt': '3',
    });

    // Echoing this back would corrupt the orchestrator's redelivery budget.
    expect(envelope.headers).not.toHaveProperty('x-vsaga-delivery-attempt');
  });

  it('mints a distinct message id per call', () => {
    const ids = new Set(
      Array.from({ length: 100 }, () => envelopeFrom('S', CORRELATION).messageId),
    );
    expect(ids.size).toBe(100);
  });
});

describe('buildHeaders', () => {
  it('emits the three reserved headers plus the envelope headers', () => {
    const envelope = envelopeFrom('ShippingService', CORRELATION, 'abc');
    const headers = buildHeaders(envelope, 'OrderShipped');

    expect(headers[CORRELATION_ID_HEADER]).toBe(CORRELATION);
    expect(headers[MESSAGE_ID_HEADER]).toBe(envelope.messageId);
    expect(headers[MESSAGE_TYPE_HEADER]).toBe('OrderShipped');
    expect(headers[SOURCE_SERVICE_HEADER]).toBe('ShippingService');
  });

  it('uses the C# short type name, not the routing key', () => {
    const headers = buildHeaders(newEnvelope(CORRELATION), 'OrderShipped');
    expect(headers[MESSAGE_TYPE_HEADER]).toBe('OrderShipped');
    expect(headers[MESSAGE_TYPE_HEADER]).not.toBe('order-shipped');
  });

  it('prefixes every header with x-vsaga-', () => {
    const headers = buildHeaders(envelopeFrom('S', CORRELATION, 'abc'), 'OrderShipped');
    for (const key of Object.keys(headers)) expect(key.startsWith('x-vsaga-')).toBe(true);
  });
});

describe('header value safety', () => {
  it.each(['bad\r\nX-Injected: 1', 'bad\nvalue', 'bad\rvalue'])('rejects %j', (value) => {
    expect(() => assertHeadersSafe({ 'x-vsaga-tenant': value })).toThrow(TypeError);
  });

  it('accepts an ordinary value', () => {
    expect(() => assertHeadersSafe({ 'x-vsaga-tenant': 'acme' })).not.toThrow();
  });
});

describe('body codec', () => {
  // Verbatim PascalCase JSON, byte-for-byte what System.Text.Json emits for
  // `record OrderShipped(Guid CorrelationId, string OrderId, string TrackingNumber)`.
  const GOLDEN =
    '{"CorrelationId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","OrderId":"ORD-1","TrackingNumber":"TRK-123456"}';

  it('encodes PascalCase with no wrapper and no $type', () => {
    const encoded = encodeBody({
      CorrelationId: CORRELATION,
      OrderId: 'ORD-1',
      TrackingNumber: 'TRK-123456',
    });

    expect(encoded.toString('utf8')).toBe(GOLDEN);
  });

  it('round-trips the golden payload', () => {
    interface OrderShippedBody {
      CorrelationId: string;
      OrderId: string;
      TrackingNumber: string;
    }

    const decoded = decodeBody<OrderShippedBody>(Buffer.from(GOLDEN, 'utf8'));
    expect(decoded.OrderId).toBe('ORD-1');
    expect(decoded.CorrelationId).toBe(CORRELATION);
    // camelCase would be the dashboard REST convention, which is NOT the broker convention.
    expect(decoded).not.toHaveProperty('orderId');
  });
});

describe('normalizeHeaders', () => {
  it('converts amqplib Buffer values to strings', () => {
    const normalized = normalizeHeaders({
      [MESSAGE_TYPE_HEADER]: Buffer.from('OrderShipped', 'utf8'),
      [CORRELATION_ID_HEADER]: CORRELATION,
    });

    expect(normalized[MESSAGE_TYPE_HEADER]).toBe('OrderShipped');
    expect(normalized[CORRELATION_ID_HEADER]).toBe(CORRELATION);
  });

  it('drops null and undefined, and tolerates no headers at all', () => {
    expect(normalizeHeaders({ a: null, b: undefined, c: 1 })).toEqual({ c: '1' });
    expect(normalizeHeaders(undefined)).toEqual({});
  });
});

describe('message()', () => {
  it('carries the short type name', () => {
    expect(message<{ OrderId: string }>('OrderShipped').name).toBe('OrderShipped');
  });

  it('rejects a routing key mistaken for a type name', () => {
    expect(() => message('order-shipped')).toThrow(TypeError);
  });
});
