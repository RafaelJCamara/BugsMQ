import http from 'node:http';
import type { AddressInfo } from 'node:net';

import type { HttpTransportOptions } from './options.js';
import { type HttpTransport, createHttpTransport } from './transport.js';

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

export async function startTestNode(): Promise<TestNode> {
  let transport: HttpTransport | undefined;

  const server = http.createServer((req, res) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end', () => {
      void (async () => {
        if (!transport) {
          res.writeHead(503);
          res.end();
          return;
        }

        const result = await transport.handleInboundRequest({
          headers: req.headers,
          body: Buffer.concat(chunks),
        });

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
      }),
  };
}
