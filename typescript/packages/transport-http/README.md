# @vsaga/transport-http

A brokerless vSaga `MessageTransport` over plain HTTP — wire-compatible with
`dotnet/src/VSaga.Transport.Http`. No RabbitMQ, no broker infrastructure: `publish`/`send` POST
directly to a configured endpoint, and a 200 response is itself the reply.

## Install

```sh
npm install @vsaga/transport-http
```

You'll also need a hosting adapter to receive inbound requests: `@vsaga/express`,
`@vsaga/fastify`, or `@vsaga/nestjs`.

## Usage

```ts
import { createHttpTransport } from '@vsaga/transport-http';
import { createParticipant } from '@vsaga/participant';

const transport = createHttpTransport({
  serviceName: 'payments',
  endpoints: { orders: 'http://orders:8080' },
  routes: { ChargeCard: ['orders'] },
});

const payments = createParticipant({ serviceName: 'payments', queue: 'payments', transport });
// ... register handlers, payments.start() ...
```

Then mount `transport`'s inbound receive endpoint with one of the hosting adapters, e.g.
`@vsaga/express`:

```ts
import express from 'express';
import { createVSagaRouter } from '@vsaga/express';

const app = express();
app.use(createVSagaRouter(transport));
app.listen(8080);
```

## Options

| Option             | Default             | Meaning                                                                                           |
| ------------------ | ------------------- | ------------------------------------------------------------------------------------------------- |
| `serviceName`      | `'vsaga-http'`      | This process's own identity, for diagnostics only.                                                |
| `endpoints`        | `{}`                | Endpoint name → base URL, e.g. `{ payments: 'http://payments:8080' }`.                            |
| `routes`           | `{}`                | Message type name → endpoint names to POST to on `publish()`. A `"*"` key is a wildcard fallback. |
| `requestTimeoutMs` | `30000`             | Per-request timeout for the outbound HTTP call.                                                   |
| `inboundPath`      | `'/vsaga/messages'` | Path this service's own receive endpoint is mapped to by a hosting adapter.                       |

## Notes

- vSaga ships no auth opinion for the inbound endpoint; apply your own via the hosting framework.
- The inbound handler reads the raw request body — hosting adapters must not let the framework's
  own body parser consume it first (each adapter's README covers the details for that framework).
- No broker underneath: a reply is fed straight back into whichever local subscriber the reply's
  message type resolves to.

## License

MIT
