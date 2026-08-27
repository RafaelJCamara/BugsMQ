# @vsaga/express

Mounts an `@vsaga/transport-http` `HttpTransport`'s inbound receive endpoint on an Express app or
Router — the TypeScript analogue of `app.MapVSagaHttp()`
(`dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs`).

## Install

```sh
npm install @vsaga/express @vsaga/transport-http express
```

## Usage

```ts
import express from 'express';
import { createHttpTransport } from '@vsaga/transport-http';
import { createVSagaRouter } from '@vsaga/express';

const transport = createHttpTransport({ serviceName: 'payments' });

const app = express();
app.use(createVSagaRouter(transport));
app.listen(8080);
```

`createVSagaRouter` adds a single `POST` route at `transport.inboundPath` (default
`/vsaga/messages`). The raw-body parser is scoped to that one route — mounting this router never
changes body-parsing behavior for the rest of your app's other routes.

### Options

```ts
createVSagaRouter(transport, { limit: '10mb' });
```

| Option  | Default | Meaning                                                                          |
| ------- | ------- | -------------------------------------------------------------------------------- |
| `limit` | `'5mb'` | Maximum accepted request body size, in any form the `bytes` package understands. |

Express's own raw-body default is 100kb, too small a ceiling to impose silently on an arbitrary
saga message.

vSaga ships no auth opinion for this endpoint; apply your own Express middleware in front of it.

## License

MIT
