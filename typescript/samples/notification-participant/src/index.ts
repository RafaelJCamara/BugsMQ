import { consoleLogger, createParticipant, httpTopologyReporter } from '@vsaga/participant';
import { message } from '@vsaga/protocol';
import { createRabbitMqTransport } from '@vsaga/transport-rabbitmq';

/**
 * The OrderProcessing sample's NotificationService, rewritten in TypeScript.
 *
 * This is the cross-runtime claim made runnable: with docker-compose.node.yml layered on, the .NET
 * sample stops registering its own NotificationParticipant and THIS process takes over the same
 * queue, the same message types, and the same replies. Nothing else in the stack is told about the
 * swap -- OrderSaga, PostShipmentChoreography and InvoiceDeliverySaga keep publishing exactly what
 * they always published, and the dashboard keeps drawing the same edges.
 *
 * The three things that make that work, none of them a translation layer:
 *   - Message types are declared by their C# short type name. `message('OrderShipped')` and .NET's
 *     `typeof(OrderShipped).Name` produce the same `x-vsaga-message-type` header and the same
 *     derived routing key.
 *   - Bodies are PascalCase JSON on the wire, which @vsaga/protocol's codec already speaks.
 *   - `ctx.reply` stamps the inbound message id as the causation id, which is what draws the edge
 *     on the Saga Map -- see envelopeFrom's own notes on why the reply's message id must be fresh.
 */

interface OrderShippedBody {
  CorrelationId: string;
  OrderId: string;
  TrackingNumber: string;
}

interface SendInvoiceEmailBody {
  CorrelationId: string;
  OrderId: string;
  InvoiceNumber: string;
}

const OrderShipped = message<OrderShippedBody>('OrderShipped');
const SendInvoiceEmail = message<SendInvoiceEmailBody>('SendInvoiceEmail');

const CustomerNotified = message<{ CorrelationId: string; OrderId: string; Channel: string }>(
  'CustomerNotified',
);
const InvoiceEmailSent = message<{ CorrelationId: string; OrderId: string }>('InvoiceEmailSent');
const InvoiceEmailBounced = message<{ CorrelationId: string; OrderId: string; Reason: string }>(
  'InvoiceEmailBounced',
);

const SERVICE_NAME = 'NotificationService';
const QUEUE_NAME = 'vsaga.participant.notification';

/** Same bounce rate the .NET participant simulates, so the failure branch shows up just as often. */
const BOUNCE_RATE = 0.15;

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

function envOrThrow(name: string): string {
  const value = process.env[name];
  if (!value)
    throw new Error(
      `${name} is required. See typescript/samples/notification-participant/README.md.`,
    );
  return value;
}

async function main(): Promise<void> {
  const transport = await createRabbitMqTransport({
    connectionString: envOrThrow('RABBITMQ__CONNECTIONSTRING'),
    clientProvidedName: SERVICE_NAME,
  });

  // Without this the two nodes this service owns render as `Unresolved` on the Saga Map -- the
  // .NET participants get the equivalent from AddVSagaTopologyRecording wrapping their transport.
  // Genuinely optional, matching every other topology reporter in this SDK: a participant that never
  // configured DASHBOARD__BASEURL/DASHBOARD__APIKEY still starts and does real work, just with
  // `Unresolved` map nodes instead of named ones -- a cold or unconfigured dashboard must never be a
  // reason this process can't come up.
  const dashboardBaseUrl = process.env['DASHBOARD__BASEURL'];
  const dashboardApiKey = process.env['DASHBOARD__APIKEY'];
  const topology =
    dashboardBaseUrl && dashboardApiKey
      ? httpTopologyReporter({ baseUrl: dashboardBaseUrl, apiKey: dashboardApiKey })
      : undefined;

  const notifications = createParticipant({
    serviceName: SERVICE_NAME,
    queue: QUEUE_NAME,
    transport,
    logger: consoleLogger,
    // Spread rather than `topology` directly: exactOptionalPropertyTypes rejects an explicit
    // `topology: undefined` on an optional property, so the key must be entirely absent, not present
    // with an undefined value, when topology reporting isn't configured.
    ...(topology ? { topology } : {}),
  });

  // Reacts on its own initiative: nobody sends a "notify the customer" command. It announces what it
  // did and is finished -- it neither knows nor cares that PostShipmentChoreography is tracking that
  // event, or that two sibling services react to the same OrderShipped in parallel.
  notifications.on(OrderShipped, async (body, ctx) => {
    // The post-shipment services use deliberately different delay ranges so their events genuinely
    // interleave rather than arriving in a fixed order -- the property the choreography is built to
    // tolerate, and one a fixed order would quietly hide.
    await delay(100 + Math.random() * 800);

    const channel = Math.random() < 0.5 ? 'email' : 'sms';
    console.log(
      `Order ${body.OrderId}: notified customer via ${channel} (tracking ${body.TrackingNumber})`,
    );
    await ctx.reply(CustomerNotified, {
      CorrelationId: body.CorrelationId,
      OrderId: body.OrderId,
      Channel: channel,
    });
  });

  // Serves InvoiceDeliverySaga, the sub-saga PostShipmentChoreography starts. No different from the
  // handler above: it neither knows nor cares that its correlation id belongs to a child saga rather
  // than to the order. A participant sees messages, not trees.
  notifications.on(SendInvoiceEmail, async (body, ctx) => {
    await delay(100 + Math.random() * 500);

    if (Math.random() < BOUNCE_RATE) {
      console.warn(`Order ${body.OrderId}: invoice ${body.InvoiceNumber} bounced`);
      await ctx.reply(InvoiceEmailBounced, {
        CorrelationId: body.CorrelationId,
        OrderId: body.OrderId,
        Reason: 'Mailbox unavailable',
      });
      return;
    }

    console.log(`Order ${body.OrderId}: invoice ${body.InvoiceNumber} emailed`);
    await ctx.reply(InvoiceEmailSent, {
      CorrelationId: body.CorrelationId,
      OrderId: body.OrderId,
    });
  });

  await notifications.start();
  console.log(`${SERVICE_NAME} (Node ${process.version}) listening on ${QUEUE_NAME}`);

  const shutdown = (signal: string): void => {
    console.log(`${signal} received, stopping ${SERVICE_NAME}.`);
    void notifications
      .stop()
      .catch((error: unknown) => console.error('Shutdown failed', error))
      .finally(() => process.exit(0));
  };

  process.on('SIGTERM', () => shutdown('SIGTERM'));
  process.on('SIGINT', () => shutdown('SIGINT'));
}

main().catch((error: unknown) => {
  console.error(error);
  process.exit(1);
});
