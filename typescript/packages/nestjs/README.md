# @vsaga/nestjs

A NestJS dynamic module mounting an `@vsaga/transport-http` `HttpTransport`'s inbound receive
endpoint on a Nest app running the **default (Express) platform**. A Nest app on the Fastify
platform should register `@vsaga/fastify`'s plugin directly via Nest's `HttpAdapterHost` instead —
this module does not attempt to be platform-agnostic.

## Install

```sh
npm install @vsaga/nestjs @vsaga/transport-http
```

## Usage

```ts
import { Module } from '@nestjs/common';
import { VSagaHttpModule } from '@vsaga/nestjs';
import { createHttpTransport } from '@vsaga/transport-http';

const transport = createHttpTransport({ serviceName: 'payments' });

@Module({
  imports: [VSagaHttpModule.forRoot({ transport })],
})
export class AppModule {}
```

```ts
// main.ts
const app = await NestFactory.create(AppModule, { rawBody: true });
await app.listen(8080);
```

### Two prerequisites, both required

- `NestFactory.create(AppModule, { rawBody: true })` — without `rawBody: true`, Nest never
  populates `req.rawBody`, and the mounted controller responds `400` to every inbound request.
- `req.rawBody` is only populated for requests with `Content-Type: application/json` (Nest's
  underlying `express.json()` `verify` hook doesn't run for any other content type). A sender must
  send `Content-Type: application/json`. A Nest app that needs to accept other content types on
  this same endpoint should mount `@vsaga/express`'s router directly instead.

Calling `VSagaHttpModule.forRoot()` twice for the same `inboundPath` in one process throws at
bootstrap — give each transport a distinct `HttpTransportOptions.inboundPath` instead of mounting
two on the same route.

## License

MIT
