import type { FastifyPluginAsync } from 'fastify';
import type { HttpTransport } from '@vsaga/transport-http';

/**
 * Registers a bound HttpTransport's inbound receive endpoint on a Fastify instance, the TS
 * analogue of dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs's `app.MapVSagaHttp()`:
 * a single POST route at `transport.inboundPath` that hands the raw request body (never
 * Fastify's own JSON-parsed body -- `handleInboundRequest` does its own JSON handling downstream)
 * to `transport.handleInboundRequest` and writes the returned status/headers/body back verbatim.
 *
 * Deliberately NOT wrapped with `fastify-plugin`: Fastify's plugin system encapsulates by default,
 * so the raw-body content-type parser override below only applies to routes registered inside this
 * same plugin function's encapsulation context (i.e. just the one POST route added here), never
 * leaking out to a host app's own JSON parsing elsewhere. Wrapping this in `fastify-plugin` would
 * break that encapsulation, so it isn't used, on purpose.
 *
 * vSaga ships no auth opinion for this endpoint; same here -- a consuming app applies its own via
 * Fastify hooks/plugins on top of `app.register(createVSagaPlugin(transport))`.
 */
export function createVSagaPlugin(transport: HttpTransport): FastifyPluginAsync {
  return async (fastify) => {
    // Fastify pre-registers its OWN default parsers for 'application/json' and 'text/plain', and
    // '*' (Fastify's documented catch-all content-type) only ever applies to a content-type with
    // no parser already registered -- it does NOT retroactively override those two defaults
    // (verified: registering only '*' still let Fastify's own text/plain parser hand back a
    // decoded string instead of raw bytes). So both of Fastify's own defaults need an explicit
    // override, plus '*' for everything else, to make every content-type hand back the raw
    // Buffer, matching Express's `raw({ type: () => true })` here, since handleInboundRequest
    // parses the wire format itself regardless of what the sender labeled it as.
    const rawBufferParser = (_request: unknown, body: Buffer, done: (err: null, body: Buffer) => void): void => {
      done(null, body);
    };
    fastify.addContentTypeParser('application/json', { parseAs: 'buffer' }, rawBufferParser);
    fastify.addContentTypeParser('text/plain', { parseAs: 'buffer' }, rawBufferParser);
    fastify.addContentTypeParser('*', { parseAs: 'buffer' }, rawBufferParser);

    fastify.post(transport.inboundPath, async (request, reply) => {
      const body = Buffer.isBuffer(request.body) ? request.body : Buffer.alloc(0);
      const result = await transport.handleInboundRequest({ headers: request.headers, body });

      void reply.status(result.status);
      if (result.headers) void reply.headers(result.headers);
      return result.body ?? Buffer.alloc(0);
    });
  };
}
