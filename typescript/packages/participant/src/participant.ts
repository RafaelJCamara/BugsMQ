import {
  type MessageTransport,
  type MessageType,
  type ReceivedMessage,
  type Subscription,
  type TopologyReporter,
  decodeBody,
  encodeBody,
  envelopeFrom,
} from '@vsaga/protocol';

import { type IdempotencyStore, InMemoryIdempotencyStore } from './idempotency.js';
import { type Logger, noopLogger } from './logger.js';

/**
 * Anything with a `parse` that throws on a bad shape -- a zod schema satisfies this natively, so
 * zod is supported without being a dependency of this package.
 */
export interface BodyValidator<TBody> {
  parse(input: unknown): TBody;
}

export interface HandlerContext {
  /**
   * Dashed Guid. This, not the CorrelationId field inside the body, is the transport correlation id
   * the engine looks up an instance by first. It is no longer the *only* thing the engine correlates
   * on: a .NET saga that has declared `CorrelateOn` can also resolve an existing instance by a
   * business key extracted from the message body when this id doesn't match one (production-readiness.md
   * §5.2/§5.3) -- e.g. a reply published under its own fresh id still reaches the same saga instance
   * that sent the original request. There is no TS-side equivalent of `CorrelateOn` yet; this field
   * remains the only correlation key a TS participant itself ever sets.
   */
  readonly correlationId: string;
  /** The INBOUND message id. Becomes the causation id of anything replied from this context. */
  readonly messageId: string;
  readonly messageTypeName: string;
  readonly headers: Readonly<Record<string, string>>;

  /** Publish a reply causally linked to the message being handled. The usual case. */
  reply<TBody>(type: MessageType<TBody>, body: TBody): Promise<void>;
  /** Publish without a causation link. For events a participant raises on its own initiative. */
  publish<TBody>(type: MessageType<TBody>, body: TBody): Promise<void>;
  /** Send straight to a named queue, bypassing bindings. */
  send<TBody>(destination: string, type: MessageType<TBody>, body: TBody): Promise<void>;

  ack(): Promise<void>;
  nack(requeue: boolean): Promise<void>;
}

export type Handler<TBody> = (body: TBody, context: HandlerContext) => Promise<void> | void;

export interface HandlerOptions<TBody> {
  /** Validates the decoded body before the handler runs. A failure is treated as a handler throw. */
  validate?: BodyValidator<TBody>;
}

export interface ParticipantOptions {
  /**
   * This participant's identity. Feeds BOTH `x-vsaga-source-service` and the topology
   * `ServiceName`, deliberately from one field: if the two ever diverge, the Saga Map grows a node
   * from the reply's source service AND a separate dangling registration, and renders a phantom
   * service. ParticipantService gets this right by construction; so does this.
   */
  readonly serviceName: string;
  /** The durable queue this participant consumes from, e.g. `vsaga.participant.shipping`. */
  readonly queue: string;
  readonly transport: MessageTransport;
  readonly logger?: Logger;
  readonly idempotency?: IdempotencyStore;
  /** Optional; without it this service renders as an `Unresolved` node on the Saga Map. */
  readonly topology?: TopologyReporter;
  /**
   * When false, the handler owns ack/nack via the context -- for participants that must ack only
   * after an external commit. Defaults to true.
   */
  readonly autoAck?: boolean;
}

interface Registration {
  readonly handler: Handler<never>;
  readonly validate?: BodyValidator<never> | undefined;
}

export interface Participant {
  on<TBody>(
    type: MessageType<TBody>,
    handler: Handler<TBody>,
    options?: HandlerOptions<TBody>,
  ): Participant;
  start(): Promise<void>;
  stop(): Promise<void>;
  readonly serviceName: string;
  readonly running: boolean;
  /** The message type names this participant handles, in registration order. */
  readonly handledTypes: readonly string[];
}

/**
 * A vSaga participant: a plain transport consumer that handles commands/events and replies.
 *
 * Participants are not sagas. They hold no state, run no timeouts, and never touch the engine --
 * exactly like the .NET sample's participants, which reference only VSaga.Abstractions. That is
 * what makes cross-runtime participation possible at all.
 *
 * Dispatch semantics are ported 1:1 from ParticipantService:
 *   - unknown message type -> ack and drop (someone else's message on a shared binding)
 *   - duplicate message id -> ack and skip
 *   - handler resolves      -> ack
 *   - handler throws        -> nack(requeue: false), i.e. straight to the dead-letter queue
 *   - a handler may legitimately reply zero times (a hung-downstream simulation, or a
 *     compensating command, which must be an idempotent no-op that does NOT reply)
 */
export function createParticipant(options: ParticipantOptions): Participant {
  const {
    serviceName,
    queue,
    transport,
    logger = noopLogger,
    idempotency = new InMemoryIdempotencyStore(),
    topology,
    autoAck = true,
  } = options;

  const registrations = new Map<string, Registration>();
  let subscription: Subscription | undefined;

  const participant: Participant = {
    serviceName,

    get running() {
      return subscription !== undefined;
    },

    get handledTypes() {
      return [...registrations.keys()];
    },

    on(type, handler, handlerOptions) {
      if (subscription) {
        throw new Error(
          `Cannot register a handler for ${type.name} after ${serviceName} has started: the queue ` +
            `bindings are declared from the registered types at start().`,
        );
      }

      if (registrations.has(type.name)) {
        throw new Error(`${serviceName} already has a handler for ${type.name}.`);
      }

      registrations.set(type.name, {
        handler: handler as Handler<never>,
        validate: handlerOptions?.validate as BodyValidator<never> | undefined,
      });

      return participant;
    },

    async start() {
      if (subscription) return;

      if (registrations.size === 0) {
        throw new Error(
          `${serviceName} has no handlers registered; starting would declare a queue with no bindings.`,
        );
      }

      const messageTypeNames = [...registrations.keys()];

      if (topology) {
        // Best-effort, exactly like .NET's: a cold dashboard must never stop a participant from
        // starting. The cost of failure is a less informative Saga Map, not a broken service.
        try {
          await topology.report(
            messageTypeNames.map((messageType) => ({
              serviceName,
              messageType,
              queueName: queue,
            })),
          );
        } catch (error) {
          logger.warn('Topology registration failed; this service will show as Unresolved', {
            serviceName,
            error,
          });
        }
      }

      subscription = await transport.subscribe(
        { consumerName: serviceName, messageTypeNames, queueNameHint: queue },
        (received) => dispatch(received),
      );

      logger.info(`${serviceName} listening on ${queue}`, { messageTypeNames });
    },

    async stop() {
      if (!subscription) return;
      await subscription.close();
      subscription = undefined;
      logger.info(`${serviceName} stopped`);
    },
  };

  async function dispatch(received: ReceivedMessage): Promise<void> {
    const registration = registrations.get(received.messageTypeName);

    if (!registration) {
      logger.debug(`${serviceName} ignoring unhandled ${received.messageTypeName}`);
      await received.ack.ack();
      return;
    }

    if (!(await idempotency.tryClaim(received.messageId))) {
      logger.debug(`${serviceName} skipping duplicate delivery of ${received.messageId}`);
      await received.ack.ack();
      return;
    }

    const context = createContext(received);

    try {
      const raw = decodeBody<unknown>(received.body);
      const body = registration.validate ? registration.validate.parse(raw) : raw;

      await registration.handler(body as never, context);

      if (autoAck) await received.ack.ack();
    } catch (error) {
      logger.error(`${serviceName} failed handling ${received.messageTypeName}`, {
        correlationId: received.correlationId,
        messageId: received.messageId,
        error,
      });

      // requeue: false -- straight to the dead-letter queue rather than a hot redelivery loop.
      if (autoAck) await received.ack.nack(false);
    }
  }

  function createContext(received: ReceivedMessage): HandlerContext {
    const publishWith = async (
      messageTypeName: string,
      body: unknown,
      causationId: string | undefined,
      destination?: string,
    ): Promise<void> => {
      const envelope = envelopeFrom(serviceName, received.correlationId, causationId);
      const encoded = encodeBody(body);

      if (destination !== undefined) {
        await transport.send(destination, messageTypeName, encoded, envelope);
      } else {
        await transport.publish(messageTypeName, encoded, envelope);
      }
    };

    return {
      correlationId: received.correlationId,
      messageId: received.messageId,
      messageTypeName: received.messageTypeName,
      headers: received.headers,

      reply: (type, body) => publishWith(type.name, body, received.messageId),
      publish: (type, body) => publishWith(type.name, body, undefined),
      send: (destination, type, body) =>
        publishWith(type.name, body, received.messageId, destination),

      ack: () => received.ack.ack(),
      nack: (requeue) => received.ack.nack(requeue),
    };
  }

  return participant;
}
