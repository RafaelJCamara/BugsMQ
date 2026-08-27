import { Controller, Post, Req, Res } from '@nestjs/common';
import type { RawBodyRequest, Type } from '@nestjs/common';
import type { Request, Response } from 'express';

import type { HttpTransport } from '@vsaga/transport-http';

/**
 * Builds a controller class bound to one HttpTransport's `inboundPath` + `handleInboundRequest`,
 * mirroring `app.MapVSagaHttp()` (dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs)
 * for Nest's default (Express) platform.
 *
 * The class is defined *inside* this factory, not at module scope, because Nest's `@Post(path)`
 * decorator needs its path argument at class-definition time, while `transport.inboundPath` is
 * only known once a transport instance exists at runtime. Each call to this factory evaluates a
 * fresh class expression, so the decorator sees the real `inboundPath` for that transport.
 *
 * Reads `req.rawBody` (populated only when the host app bootstraps with
 * `NestFactory.create(AppModule, { rawBody: true })`), never `req.body` -- `handleInboundRequest`
 * does its own JSON parsing downstream and must see the exact bytes that were on the wire.
 *
 * `req.rawBody` is itself only populated by Nest's underlying `express.json()` middleware's
 * `verify` hook, which only runs for a request whose Content-Type matches `application/json` --
 * for any other (or missing) Content-Type, body-parser skips reading the body entirely and
 * `rawBody` stays `undefined`. Rather than silently substituting an empty buffer in that case
 * (which would look like a genuinely empty message and still respond 202/200), this responds 400
 * so the caller sees a clear rejection instead of a message that silently vanished.
 */
export function createVSagaHttpController(transport: HttpTransport): Type<object> {
  @Controller()
  class VSagaHttpController {
    @Post(transport.inboundPath)
    async handleInbound(@Req() req: RawBodyRequest<Request>, @Res() res: Response): Promise<void> {
      if (req.rawBody === undefined) {
        res.status(400).end();
        return;
      }

      const result = await transport.handleInboundRequest({
        headers: req.headers,
        body: req.rawBody,
      });

      res.status(result.status);
      if (result.headers) {
        for (const [key, value] of Object.entries(result.headers)) res.setHeader(key, value);
      }
      res.end(result.body);
    }
  }

  return VSagaHttpController;
}
