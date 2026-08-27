import type { AddressInfo } from 'node:net';

import 'reflect-metadata';
import type { INestApplication } from '@nestjs/common';
import { Test } from '@nestjs/testing';
import {
  CORRELATION_ID_HEADER,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  type ReceivedMessage,
  envelopeFrom,
  newCorrelationId,
  newEnvelope,
} from '@vsaga/protocol';
import { createHttpTransport, type HttpTransport } from '@vsaga/transport-http';
import { afterEach, describe, expect, it } from 'vitest';

import { VSagaHttpModule } from './module.js';

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

/** Boots a real Nest (Express platform) app on an ephemeral port with VSagaHttpModule mounted, `rawBody: true` wired through. */
async function startApp(transport: HttpTransport): Promise<{ app: INestApplication; baseUrl: string }> {
  const moduleRef = await Test.createTestingModule({
    imports: [VSagaHttpModule.forRoot({ transport })],
  }).compile();

  const app = moduleRef.createNestApplication({ rawBody: true });
  await app.init();
  await app.listen(0);

  const address = app.getHttpServer().address() as AddressInfo;
  return { app, baseUrl: `http://127.0.0.1:${address.port}` };
}

describe('VSagaHttpModule', () => {
  const apps: INestApplication[] = [];

  afterEach(async () => {
    await Promise.all(apps.splice(0).map((app) => app.close()));
  });

  it('delivers a well-formed inbound request to the bound transport\'s local subscriber and responds 202', async () => {
    const transport = createHttpTransport();
    const { app, baseUrl } = await startApp(transport);
    apps.push(app);

    const received = deferred<ReceivedMessage>();
    await transport.subscribe(
      { consumerName: 'TestConsumer', messageTypeNames: ['PingMessage'], queueNameHint: 'ping-queue' },
      async (message) => received.resolve(message),
    );

    const correlationId = newCorrelationId();
    const envelope = newEnvelope(correlationId);
    const body = Buffer.from(JSON.stringify({ text: 'hi' }));

    const response = await fetch(`${baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        [MESSAGE_TYPE_HEADER]: 'PingMessage',
        [MESSAGE_ID_HEADER]: envelope.messageId,
        [CORRELATION_ID_HEADER]: correlationId,
      },
      body,
    });
    await response.arrayBuffer();

    expect(response.status).toBe(202);
    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
    expect(JSON.parse(message.body.toString('utf8'))).toEqual({ text: 'hi' });
  });

  it('responds 400 when required x-vsaga- headers are missing', async () => {
    const transport = createHttpTransport();
    const { app, baseUrl } = await startApp(transport);
    apps.push(app);

    const response = await fetch(`${baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: Buffer.from('{}'),
    });
    await response.arrayBuffer();

    expect(response.status).toBe(400);
  });

  it('a handler publishing an unroutable reply produces a synchronous 200 with the reply\'s headers and body', async () => {
    const transport = createHttpTransport();
    const { app, baseUrl } = await startApp(transport);
    apps.push(app);

    // 'Reply' has no route/local subscriber -- unroutable, so it's captured as this handler's own
    // synchronous reply to the inbound 'Command' it's currently handling.
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

    const response = await fetch(`${baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        [MESSAGE_TYPE_HEADER]: 'Command',
        [MESSAGE_ID_HEADER]: envelope.messageId,
        [CORRELATION_ID_HEADER]: correlationId,
      },
      body: Buffer.from(JSON.stringify({ text: 'charge' })),
    });

    expect(response.status).toBe(200);
    expect(response.headers.get(MESSAGE_TYPE_HEADER)).toBe('Reply');
    expect(response.headers.get(CORRELATION_ID_HEADER)).toBe(correlationId);
    expect(await response.json()).toEqual({ text: 'ok' });
  });

  /**
   * Regression test for a bug caught by review: req.rawBody is only populated by Nest's
   * underlying express.json() verify hook, which only runs for Content-Type: application/json --
   * for any other content-type it silently stayed undefined and the controller substituted an
   * empty buffer, so a message with the wrong Content-Type looked like a successful, empty
   * delivery instead of failing loudly.
   */
  it('responds 400 when rawBody was never populated (Content-Type other than application/json)', async () => {
    const transport = createHttpTransport();
    const { app, baseUrl } = await startApp(transport);
    apps.push(app);

    const correlationId = newCorrelationId();
    const envelope = newEnvelope(correlationId);

    const response = await fetch(`${baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: {
        'content-type': 'text/plain',
        [MESSAGE_TYPE_HEADER]: 'PingMessage',
        [MESSAGE_ID_HEADER]: envelope.messageId,
        [CORRELATION_ID_HEADER]: correlationId,
      },
      body: Buffer.from(JSON.stringify({ text: 'hi' })),
    });

    expect(response.status).toBe(400);
  });

  /**
   * Regression test for a bug caught by review: forRoot() had no guard against being registered
   * twice for the same inboundPath, which used to boot successfully and silently make the second
   * transport's endpoint permanently unreachable behind the first.
   */
  it('forRoot() throws when called twice for the same inboundPath in one process', () => {
    const transportA = createHttpTransport({ inboundPath: '/vsaga/dup-test' });
    const transportB = createHttpTransport({ inboundPath: '/vsaga/dup-test' });

    VSagaHttpModule.forRoot({ transport: transportA });

    expect(() => VSagaHttpModule.forRoot({ transport: transportB })).toThrow(/already called/);
  });
});
