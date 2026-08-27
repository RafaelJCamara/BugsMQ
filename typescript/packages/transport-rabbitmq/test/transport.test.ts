import { describe, expect, it } from 'vitest';
import { CORRELATION_ID_HEADER, EMPTY_CORRELATION_ID } from '@vsaga/protocol';

import { resolveOptions } from '../src/options.js';
import { parseCorrelationId } from '../src/transport.js';

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
