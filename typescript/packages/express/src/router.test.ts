import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';

import express from 'express';
import { afterEach, describe, expect, it } from 'vitest';
import {
  buildHeaders,
  envelopeFrom,
  MESSAGE_TYPE_HEADER,
  newCorrelationId,
  newEnvelope,
  type ReceivedMessage,
} from '@vsaga/protocol';
import { type HttpTransport, createHttpTransport } from '@vsaga/transport-http';

import { type CreateVSagaRouterOptions, createVSagaRouter } from './router.js';

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

/**
 * A real localhost server hosting an Express app with `createVSagaRouter` mounted -- proving this
 * adapter's own wiring (real request -> handleInboundRequest -> real response), not
 * @vsaga/transport-http's routing/sync-reply semantics, which its own suite already covers.
 */
async function startApp(
  routerOptions?: CreateVSagaRouterOptions,
): Promise<{ transport: HttpTransport; baseUrl: string; close: () => Promise<void> }> {
  const transport = createHttpTransport();
  const app = express();
  app.use(createVSagaRouter(transport, routerOptions));

  const server: Server = app.listen(0, '127.0.0.1');
  await new Promise<void>((resolve) => server.once('listening', resolve));
  const address = server.address() as AddressInfo;

  return {
    transport,
    baseUrl: `http://127.0.0.1:${address.port}${transport.inboundPath}`,
    close: () =>
      new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
      }),
  };
}

describe('createVSagaRouter (Express)', () => {
  const apps: Array<{ close: () => Promise<void> }> = [];

  async function app(routerOptions?: CreateVSagaRouterOptions): Promise<Awaited<ReturnType<typeof startApp>>> {
    const started = await startApp(routerOptions);
    apps.push(started);
    return started;
  }

  afterEach(async () => {
    await Promise.all(apps.splice(0).map((a) => a.close()));
  });

  it('delivers a well-formed inbound request to the registered local subscriber and responds 202', async () => {
    const { transport, baseUrl } = await app();

    const received = deferred<ReceivedMessage>();
    await transport.subscribe(
      { consumerName: 'TestConsumer', messageTypeNames: ['PingMessage'], queueNameHint: 'ping-queue' },
      async (message) => received.resolve(message),
    );

    const correlationId = newCorrelationId();
    const body = Buffer.from(JSON.stringify({ text: 'hello' }));

    const response = await fetch(baseUrl, {
      method: 'POST',
      headers: buildHeaders(newEnvelope(correlationId), 'PingMessage'),
      body,
    });

    expect(response.status).toBe(202);
    await response.body?.cancel();

    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
    expect(message.messageTypeName).toBe('PingMessage');
    expect(JSON.parse(message.body.toString('utf8'))).toEqual({ text: 'hello' });
  });

  it('responds 400 when the required x-vsaga- headers are missing', async () => {
    const { baseUrl } = await app();

    const response = await fetch(baseUrl, {
      method: 'POST',
      headers: { [MESSAGE_TYPE_HEADER]: 'PingMessage' },
      body: Buffer.from('{}'),
    });

    expect(response.status).toBe(400);
    await response.body?.cancel();
  });

  it('a handler publishing an unroutable reply produces a 200 response carrying the reply headers and body', async () => {
    const { transport, baseUrl } = await app();

    // 'Reply' has no route and no local subscriber, so publishing it from inside the 'Command'
    // handler is captured as this inbound request's own synchronous reply.
    await transport.subscribe(
      { consumerName: 'Receiver', messageTypeNames: ['Command'], queueNameHint: 'command-queue' },
      async (message) => {
        await transport.publish(
          'Reply',
          Buffer.from(JSON.stringify({ text: 'ok' })),
          envelopeFrom('test-service', message.correlationId, message.messageId),
        );
      },
    );

    const correlationId = newCorrelationId();
    const response = await fetch(baseUrl, {
      method: 'POST',
      headers: buildHeaders(newEnvelope(correlationId), 'Command'),
      body: Buffer.from(JSON.stringify({ text: 'charge' })),
    });

    expect(response.status).toBe(200);
    expect(response.headers.get(MESSAGE_TYPE_HEADER)).toBe('Reply');
    expect(await response.json()).toEqual({ text: 'ok' });
  });

  /**
   * Regression test for a bug caught by review: express.raw()'s undocumented-here default body
   * limit is 100kb, well under a plausible saga message size, and createVSagaRouter exposed no
   * way to raise it. Confirms the new default (5mb) accepts a body well over 100kb, and that the
   * `limit` option is actually wired through to express.raw().
   */
  it('accepts a body over express.raw()\'s 100kb default, and honors a configured limit', async () => {
    const { transport, baseUrl } = await app();

    const received = deferred<ReceivedMessage>();
    await transport.subscribe(
      { consumerName: 'BigConsumer', messageTypeNames: ['PingMessage'], queueNameHint: 'big-queue' },
      async (message) => received.resolve(message),
    );

    const bigBody = Buffer.from(JSON.stringify({ text: 'x'.repeat(200_000) }));
    const response = await fetch(baseUrl, {
      method: 'POST',
      headers: buildHeaders(newEnvelope(newCorrelationId()), 'PingMessage'),
      body: bigBody,
    });

    expect(response.status).toBe(202);
    await response.body?.cancel();
    await received.promise;

    const { baseUrl: smallLimitBaseUrl } = await app({ limit: '1kb' });
    const rejectedResponse = await fetch(smallLimitBaseUrl, {
      method: 'POST',
      headers: buildHeaders(newEnvelope(newCorrelationId()), 'PingMessage'),
      body: bigBody,
    });

    expect(rejectedResponse.status).toBe(413);
    await rejectedResponse.body?.cancel();
  });
});
