# Notification participant (cross-runtime sample)

The OrderProcessing sample's `NotificationService`, rewritten in TypeScript. Run it and a Node
process handles messages published by .NET sagas, replying with messages those sagas resume on —
with no translation layer, no shared schema registry, and no code change on the .NET side beyond a
flag that stops it registering the participant this one replaces.

## Run it

```bash
docker compose -f docker-compose.yml -f docker-compose.node.yml up -d --build
docker compose logs -f notification-participant
```

You'll see this process reporting work the .NET sagas asked for:

```
NotificationService (Node v22.x) listening on vsaga.participant.notification
Order ORD-20260828-0001: notified customer via sms (tracking TRK-471813)
Order ORD-20260828-0001: invoice INV-299249 emailed
```

Open http://localhost:4200, pick a `PostShipmentChoreography` saga, and `NotificationService` is a
named node on its Saga Map exactly as it was when .NET owned it — the dashboard has no idea the
service changed language.

## What it handles

| Receives           | Published by                            | Replies                                                       |
| ------------------ | --------------------------------------- | ------------------------------------------------------------- |
| `OrderShipped`     | `ShippingService`, as a broadcast event | `CustomerNotified`                                            |
| `SendInvoiceEmail` | `InvoiceDeliverySaga`, as a command     | `InvoiceEmailSent`, or `InvoiceEmailBounced` ~15% of the time |

The bounce rate is deliberate and matches the .NET participant's: it keeps `InvoiceDeliverySaga`'s
failure branch exercised on every run instead of leaving it a path nobody sees.

## Why it works

Three things carry the compatibility, all of them in `@vsaga/protocol`:

1. **Message identity is the C# short type name.** `message('OrderShipped')` produces the same
   `x-vsaga-message-type` header and the same derived routing key as .NET's `typeof(OrderShipped).Name`.
   Declare the same name on both sides and the message round-trips.
2. **Bodies are PascalCase JSON**, which the protocol codec reads and writes natively.
3. **`ctx.reply` stamps the inbound message id as the causation id** and mints a _fresh_ message id
   for the reply. Both halves matter: causation is what draws the edge on the Saga Map, and reusing
   the inbound id would make the orchestrator dedupe the reply away, leaving the saga to sit until
   its timeout looking like a hung participant.

## Configuration

| Variable                     | Required? | Meaning                                                                                            |
| ---------------------------- | --------- | -------------------------------------------------------------------------------------------------- |
| `RABBITMQ__CONNECTIONSTRING` | Yes       | AMQP URL, e.g. `amqp://guest:guest@rabbitmq:5672/`. Process exits at startup naming it if missing. |
| `DASHBOARD__BASEURL`         | No        | Dashboard API base URL, for topology registration.                                                 |
| `DASHBOARD__APIKEY`          | No        | Dashboard API key.                                                                                 |

Topology registration goes over the Dashboard API (`POST /api/topology/registrations`) rather than
straight to Postgres, so this service needs no database credentials and no copy of the schema. Set
**both** `DASHBOARD__BASEURL` and `DASHBOARD__APIKEY` to enable it, or leave either unset to skip it —
the participant still runs and does real work either way; its nodes just render as `Unresolved` on the
Saga Map without them.

## Running it outside Docker

```bash
cd typescript
npm install
npm run build
export RABBITMQ__CONNECTIONSTRING='amqp://guest:guest@localhost:5672/'
export DASHBOARD__BASEURL='http://localhost:5080'
export DASHBOARD__APIKEY='dev-local-only-change-me'
npm start --workspace @vsaga/sample-notification-participant
```

(bash/Git Bash/WSL syntax above — in PowerShell, use `$env:RABBITMQ__CONNECTIONSTRING = '...'` etc.
instead of `export`.)

Start the rest of the stack with `docker compose up -d --build` first, and set
`Participants__NotificationInProcess=false` on `order-processing` so the two don't both consume the
queue — see [`../../../docker-compose.node.yml`](../../../docker-compose.node.yml) for why that
split matters.

## See also

- [`docs/typescript-participants.md`](../../../docs/typescript-participants.md) — the SDK reference.
- [`@vsaga/participant`](../../packages/participant/README.md) — dispatch semantics and options.
- [`@vsaga/transport-rabbitmq`](../../packages/transport-rabbitmq/README.md) — the transport this uses.
