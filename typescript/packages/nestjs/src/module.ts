import { Module } from '@nestjs/common';
import type { DynamicModule, OnModuleDestroy } from '@nestjs/common';

import type { HttpTransport } from '@vsaga/transport-http';

import { createVSagaHttpController } from './controller.js';

export interface VSagaHttpModuleOptions {
  readonly transport: HttpTransport;
}

/**
 * Tracks which `inboundPath`s already have a controller mounted in this process, so a second
 * `forRoot()` call for the same path fails loudly at bootstrap instead of silently mounting an
 * unreachable second controller behind the first (Express/Nest route matching takes the first
 * registered match; ASP.NET Core's `MapVSagaHttp()` at least throws `AmbiguousMatchException` for
 * the equivalent misconfiguration -- this is louder still, since it fails at startup rather than
 * on the first request).
 */
const registeredInboundPaths = new Set<string>();

class VSagaHttpPathRegistration implements OnModuleDestroy {
  readonly #path: string;

  constructor(path: string) {
    this.#path = path;
  }

  onModuleDestroy(): void {
    registeredInboundPaths.delete(this.#path);
  }
}

/**
 * Mounts an `HttpTransport`'s (`@vsaga/transport-http`) inbound receive endpoint on a Nest app
 * running the DEFAULT (Express) platform, i.e. `NestFactory.create(AppModule, ...)` with no
 * explicit `FastifyAdapter`. A Nest app on the Fastify platform should instead register
 * `@vsaga/fastify`'s plugin directly via Nest's `HttpAdapterHost` -- this module does not attempt
 * to be platform-agnostic.
 *
 * IMPORTANT, two prerequisites on the host app's `NestFactory.create(AppModule, options)`:
 *  - It MUST include `{ rawBody: true }`. Without it, Nest never populates `req.rawBody`, and the
 *    controller responds 400 to every inbound request rather than silently reaching
 *    `handleInboundRequest` with an empty body.
 *  - `req.rawBody` itself is only populated for requests whose Content-Type is
 *    `application/json` (Nest's underlying `express.json()` `verify` hook does not run for any
 *    other content-type) -- a sender must send `Content-Type: application/json`, matching every
 *    example in docs/design/http-based-sagas.md §4.2. A Nest app needing to accept other content-types
 *    on this same endpoint should use `@vsaga/express`'s router directly instead, which reads the
 *    raw body regardless of Content-Type.
 */
@Module({})
export class VSagaHttpModule {
  static forRoot(options: VSagaHttpModuleOptions): DynamicModule {
    const path = options.transport.inboundPath;

    if (registeredInboundPaths.has(path)) {
      throw new Error(
        `VSagaHttpModule.forRoot() was already called for inboundPath '${path}' in this process. ` +
          'Two transports mounted on the same path would silently make the second one unreachable ' +
          '-- give each transport a distinct HttpTransportOptions.inboundPath instead.',
      );
    }
    registeredInboundPaths.add(path);

    return {
      module: VSagaHttpModule,
      controllers: [createVSagaHttpController(options.transport)],
      providers: [
        {
          provide: VSagaHttpPathRegistration,
          useFactory: () => new VSagaHttpPathRegistration(path),
        },
      ],
    };
  }
}
