# @vsaga/fastify

Registers an `@vsaga/transport-http` `HttpTransport`'s inbound receive endpoint as a Fastify
plugin — the TypeScript analogue of `app.MapVSagaHttp()`
(`dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs`).

## Install

```sh
npm install @vsaga/fastify @vsaga/transport-http fastify
```

## Usage

```ts
import Fastify from 'fastify';
import { createHttpTransport } from '@vsaga/transport-http';
import { createVSagaPlugin } from '@vsaga/fastify';

const transport = createHttpTransport({ serviceName: 'payments' });

const app = Fastify();
await app.register(createVSagaPlugin(transport));
await app.listen({ port: 8080 });
```

`createVSagaPlugin` registers a single `POST` route at `transport.inboundPath` (default
`/vsaga/messages`) and overrides Fastify's content-type parsers _inside its own plugin
encapsulation only_ so the route receives the raw request body — never Fastify's own JSON-parsed
body, since the transport does its own wire-format handling downstream. Registering it this way
(rather than with `fastify-plugin`) means it never leaks that override into the rest of your app's
routes.

vSaga ships no auth opinion for this endpoint; apply your own via Fastify hooks or plugins layered
on top of `app.register(createVSagaPlugin(transport))`.

## License

MIT
