import http from 'node:http';
import type { AddressInfo } from 'node:net';

import { afterEach, describe, expect, it } from 'vitest';

import { httpTopologyReporter } from '../src/topology-reporter.js';

interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
}

function deferred<T>(): Deferred<T> {
  let resolveFn!: (value: T) => void;
  const promise = new Promise<T>((resolve) => {
    resolveFn = resolve;
  });
  return { promise, resolve: (value) => resolveFn(value) };
}

interface CapturedRequest {
  readonly method: string | undefined;
  readonly url: string | undefined;
  readonly headers: http.IncomingHttpHeaders;
  readonly body: string;
}

interface TestServer {
  readonly baseUrl: string;
  close(): Promise<void>;
}

/**
 * A real localhost HTTP server, not a mocked fetch: httpTopologyReporter's whole job is building
 * the right request (method, headers, JSON body, path) and reacting to the real response, which a
 * fetch mock would only prove agrees with itself. Mirrors packages/transport-http/test/test-node.ts.
 */
async function startServer(
  handle: (req: http.IncomingMessage, res: http.ServerResponse, body: string) => void,
): Promise<TestServer> {
  const server = http.createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => handle(req, res, Buffer.concat(chunks).toString('utf8')));
  });

  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address() as AddressInfo;

  return {
    baseUrl: `http://127.0.0.1:${address.port}`,
    close: () =>
      new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
      }),
  };
}

describe('httpTopologyReporter', () => {
  let server: TestServer | undefined;

  afterEach(async () => {
    await server?.close();
    server = undefined;
  });

  it('POSTs the registrations as JSON to the default path with the api key header', async () => {
    const captured = deferred<CapturedRequest>();
    server = await startServer((req, res, body) => {
      captured.resolve({ method: req.method, url: req.url, headers: req.headers, body });
      res.writeHead(204);
      res.end();
    });

    const reporter = httpTopologyReporter({ baseUrl: server.baseUrl, apiKey: 'secret-key' });
    const registrations = [
      { serviceName: 'OrdersApi', messageType: 'OrderShipped', queueName: 'orders-queue' },
    ];
    await reporter.report(registrations);

    const request = await captured.promise;
    expect(request.method).toBe('POST');
    expect(request.url).toBe('/api/topology/registrations');
    expect(request.headers['content-type']).toBe('application/json');
    expect(request.headers['x-api-key']).toBe('secret-key');
    expect(JSON.parse(request.body)).toEqual(registrations);
  });

  it('honours a custom path option instead of the default', async () => {
    const captured = deferred<string | undefined>();
    server = await startServer((req, res) => {
      captured.resolve(req.url);
      res.writeHead(204);
      res.end();
    });

    const reporter = httpTopologyReporter({
      baseUrl: server.baseUrl,
      apiKey: 'k',
      path: '/custom/registrations',
    });
    await reporter.report([]);

    expect(await captured.promise).toBe('/custom/registrations');
  });

  it('resolves without throwing on a 2xx response', async () => {
    server = await startServer((_req, res) => {
      res.writeHead(200);
      res.end();
    });

    const reporter = httpTopologyReporter({ baseUrl: server.baseUrl, apiKey: 'k' });
    await expect(reporter.report([])).resolves.toBeUndefined();
  });

  it('throws an error naming the status and URL when the server rejects the registration', async () => {
    server = await startServer((_req, res) => {
      res.writeHead(500, 'Internal Server Error');
      res.end();
    });

    const reporter = httpTopologyReporter({ baseUrl: server.baseUrl, apiKey: 'k' });
    await expect(reporter.report([])).rejects.toThrow(
      new RegExp(
        `${server.baseUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/api/topology/registrations returned 500`,
      ),
    );
  });

  it('aborts and rejects once timeoutMs elapses without a response, instead of hanging forever', async () => {
    server = await startServer(() => {
      // Deliberately never calls res.end() -- proves the AbortSignal.timeout actually tears the
      // request down rather than the reporter (and its caller) hanging indefinitely.
    });

    const reporter = httpTopologyReporter({ baseUrl: server.baseUrl, apiKey: 'k', timeoutMs: 50 });
    await expect(reporter.report([])).rejects.toThrow();
  });
});
