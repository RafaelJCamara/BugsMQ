import { Router, raw } from 'express';
import type { NextFunction, Request, Response } from 'express';
import type { HttpTransport } from '@vsaga/transport-http';

export interface CreateVSagaRouterOptions {
  /**
   * Maximum accepted request body size, in any form `bytes` (npm) understands, e.g. `'5mb'`.
   * Express's own raw-body default is 100kb -- too small a ceiling to impose silently on an
   * arbitrary saga message, unlike the .NET reference (Kestrel's much larger default, with no cap
   * imposed by `VSagaHttpEndpointExtensions` itself). Defaults to `'5mb'`; raise or lower to match
   * your own message sizes.
   */
  readonly limit?: string | number;
}

/**
 * Mounts one HttpTransport's inbound receive endpoint on an Express Router, the TypeScript
 * analogue of `app.MapVSagaHttp()`
 * (dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs): a POST route at
 * `transport.inboundPath` that reads the raw request body as a Buffer -- never framework-parsed
 * JSON, since handleInboundRequest does its own JSON handling downstream -- and writes the
 * returned status/headers/body straight back onto the response. vSaga ships no auth opinion for
 * this endpoint; neither does this adapter.
 *
 * The raw-body parser is scoped to this one route (not `app.use()`) so mounting this router never
 * changes body-parsing behavior for the rest of a host app's other routes.
 */
export function createVSagaRouter(
  transport: HttpTransport,
  options: CreateVSagaRouterOptions = {},
): Router {
  const router = Router();

  router.post(
    transport.inboundPath,
    raw({ type: () => true, limit: options.limit ?? '5mb' }),
    (req: Request, res: Response, next: NextFunction) => {
      void handleRequest(transport, req, res).catch(next);
    },
  );

  return router;
}

async function handleRequest(transport: HttpTransport, req: Request, res: Response): Promise<void> {
  const body = Buffer.isBuffer(req.body) ? req.body : Buffer.alloc(0);
  const result = await transport.handleInboundRequest({ headers: req.headers, body });

  res.status(result.status);
  if (result.headers) {
    for (const [key, value] of Object.entries(result.headers)) res.setHeader(key, value);
  }
  res.end(result.body);
}
