import Fastify from 'fastify';
import { afterEach, describe, expect, it } from 'vitest';
import {
  CORRELATION_ID_HEADER,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  type ReceivedMessage,
  buildHeaders,
  envelopeFrom,
  newCorrelationId,
  newEnvelope,
} from '@vsaga/protocol';
import { type HttpTransport, createHttpTransport } from '@vsaga/transport-http';

import { createVSagaPlugin } from '../src/plugin.js';

/** TaskCompletionSource-alike, matching transport-http's own test style. */
interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
}

function deferred<T>(): Deferred<T> {
  let resolveFn!: (value: T) => void;
  const promise = new Promise<T>((resolve) => {
    resolveFn = resolve;
  });
  return { promise, resolve: resolveFn };
}

/**
 * Boots a real Fastify app on an ephemeral port with `createVSagaPlugin(transport)` registered,
 * mirroring transport-http's own test-node.ts style (real sockets, global fetch) rather than
 * Fastify's inject().
 */
async function startApp(
  transport: HttpTransport,
): Promise<{ baseUrl: string; close: () => Promise<void> }> {
  const app = Fastify();
  await app.register(createVSagaPlugin(transport));
  const baseUrl = await app.listen({ port: 0, host: '127.0.0.1' });
  return { baseUrl, close: () => app.close() };
}

describe('createVSagaPlugin', () => {
  const apps: Array<{ close: () => Promise<void> }> = [];

  afterEach(async () => {
    await Promise.all(apps.splice(0).map((a) => a.close()));
  });

  it('delivers a well-formed request to a registered local subscriber and responds 202', async () => {
    const transport = createHttpTransport();
    const app = await startApp(transport);
    apps.push(app);

    const received = deferred<ReceivedMessage>();
    await transport.subscribe(
      {
        consumerName: 'TestConsumer',
        messageTypeNames: ['PingMessage'],
        queueNameHint: 'ping-queue',
      },
      async (message) => received.resolve(message),
    );

    const correlationId = newCorrelationId();
    const envelope = newEnvelope(correlationId);
    const body = Buffer.from(JSON.stringify({ text: 'hello' }));

    const response = await fetch(`${app.baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...buildHeaders(envelope, 'PingMessage') },
      body,
    });

    expect(response.status).toBe(202);
    await response.body?.cancel();

    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
    expect(message.messageTypeName).toBe('PingMessage');
    expect(JSON.parse(message.body.toString('utf8'))).toEqual({ text: 'hello' });
  });

  it('responds 400 for a request missing the required x-vsaga- headers', async () => {
    const transport = createHttpTransport();
    const app = await startApp(transport);
    apps.push(app);

    const response = await fetch(`${app.baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: Buffer.from(JSON.stringify({ text: 'hello' })),
    });

    expect(response.status).toBe(400);
    await response.body?.cancel();
  });

  it('completes a synchronous-reply round trip: 200 with the reply headers and body', async () => {
    const transport = createHttpTransport();
    const app = await startApp(transport);
    apps.push(app);

    // No local subscriber/route for 'Reply' -- unroutable, so it's captured as this inbound
    // request's own synchronous reply.
    await transport.subscribe(
      { consumerName: 'Receiver', messageTypeNames: ['Command'], queueNameHint: 'command-queue' },
      async (message) => {
        await transport.publish(
          'Reply',
          Buffer.from(JSON.stringify({ text: 'ok' })),
          envelopeFrom('Receiver', message.correlationId, message.messageId),
        );
      },
    );

    const correlationId = newCorrelationId();
    const envelope = newEnvelope(correlationId);

    const response = await fetch(`${app.baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...buildHeaders(envelope, 'Command') },
      body: Buffer.from(JSON.stringify({ text: 'charge' })),
    });

    expect(response.status).toBe(200);
    expect(response.headers.get(MESSAGE_TYPE_HEADER)).toBe('Reply');
    expect(response.headers.get(CORRELATION_ID_HEADER)).toBe(correlationId);
    expect(response.headers.get(MESSAGE_ID_HEADER)).toBeTruthy();
    expect(await response.json()).toEqual({ text: 'ok' });
  });

  /**
   * Regression test for a bug caught by review: the content-type parser was originally scoped to
   * 'application/json' only, so any other content-type either lost its body silently (still 202)
   * or was hard-rejected by Fastify's own 415 before reaching the route at all.
   */
  it('reads the raw body regardless of Content-Type', async () => {
    const transport = createHttpTransport();
    const app = await startApp(transport);
    apps.push(app);

    const received = deferred<ReceivedMessage>();
    await transport.subscribe(
      {
        consumerName: 'TestConsumer',
        messageTypeNames: ['PingMessage'],
        queueNameHint: 'ping-queue-2',
      },
      async (message) => received.resolve(message),
    );

    const correlationId = newCorrelationId();
    const envelope = newEnvelope(correlationId);
    const body = Buffer.from(JSON.stringify({ text: 'plain' }));

    const response = await fetch(`${app.baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: { 'content-type': 'text/plain', ...buildHeaders(envelope, 'PingMessage') },
      body,
    });

    expect(response.status).toBe(202);
    await response.body?.cancel();

    const message = await received.promise;
    expect(JSON.parse(message.body.toString('utf8'))).toEqual({ text: 'plain' });
  });
});
