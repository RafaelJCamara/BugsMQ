import http from 'node:http';
import type { AddressInfo } from 'node:net';

import type { HttpTransportOptions } from '../src/options.js';
import { type HttpTransport, createHttpTransport } from '../src/transport.js';

/**
 * A real localhost HTTP server wired to one HttpTransport's handleInboundRequest -- the TS
 * equivalent of dotnet/tests/VSaga.Transport.Http.Tests/HttpTestNode.cs, minus the synthetic
 * hostname registry: a real socket on an ephemeral port needs no in-memory routing trick to be
 * addressable by another node's `fetch`.
 *
 * Test-only on purpose: not exported from the package index, not published (never imported by
 * index.ts, so it never reaches dist/ regardless).
 *
 * Two-phase because two nodes can need each other's baseUrl in their own HttpTransportOptions
 * (a node whose Routes point back at its caller) -- the listener has to exist to hand out a
 * baseUrl before the options that reference it can be built, so `bind()` is separate from
 * `startTestNode()` and swaps in the real transport once every node's baseUrl is known.
 */
export interface TestNode {
  readonly baseUrl: string;
  bind(options?: HttpTransportOptions): HttpTransport;
  close(): Promise<void>;
}

/** A canned response, for failure-path tests that need a reply no real vSaga endpoint would produce. */
export interface CannedResponse {
  readonly status: number;
  readonly headers?: Readonly<Record<string, string>>;
  readonly body?: Buffer | string;
}

export interface TestNodeOptions {
  /**
   * Replaces the transport-backed inbound handling entirely, so a test can serve a 500, a malformed
   * 200, or -- by returning a promise that never settles -- nothing at all. `bind()` is still
   * available on such a node, but only so the test can hand its `baseUrl` to a sender; its own
   * transport never sees a request.
   */
  readonly respondWith?: (request: http.IncomingMessage, body: Buffer) => Promise<CannedResponse>;
}

export async function startTestNode(options: TestNodeOptions = {}): Promise<TestNode> {
  let transport: HttpTransport | undefined;

  const server = http.createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => {
      void (async () => {
        const body = Buffer.concat(chunks);

        if (options.respondWith) {
          const canned = await options.respondWith(req, body);
          res.writeHead(canned.status, canned.headers ?? {});
          res.end(canned.body);
          return;
        }

        if (!transport) {
          res.writeHead(503);
          res.end();
          return;
        }

        const result = await transport.handleInboundRequest({ headers: req.headers, body });

        res.writeHead(result.status, result.headers ?? {});
        res.end(result.body);
      })();
    });
  });

  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  const address = server.address() as AddressInfo;
  const baseUrl = `http://127.0.0.1:${address.port}`;

  return {
    baseUrl,

    bind(options) {
      transport = createHttpTransport(options);
      return transport;
    },

    close: () =>
      new Promise<void>((resolve, reject) => {
        server.close((error) => (error ? reject(error) : resolve()));
        // `close()` alone only stops new connections and then waits for the open ones to end, which
        // never happens for a `respondWith` that deliberately never responds -- afterEach would hang
        // instead of failing.
        server.closeAllConnections();
      }),
  };
}
