import type { MessageEnvelope } from './envelope.js';

/**
 * Port of IMessageTransport (dotnet/src/VSaga.Abstractions/Transport/IMessageTransport.cs).
 *
 * This interface lives in the leaf package for the same reason its C# counterpart lives in
 * VSaga.Abstractions: transports must be able to implement it without seeing the participant runtime
 * or a future @vsaga/core. That is the seam that will let an orchestrator land later and reuse
 * @vsaga/transport-rabbitmq unchanged.
 */
export interface MessageTransport {
  /** Publish to the topic exchange, routed by `toRoutingKey(messageTypeName)`. */
  publish(
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal?: AbortSignal,
  ): Promise<void>;

  /**
   * Send straight to a named destination. Over AMQP this is the default exchange with
   * `routingKey = destination` (i.e. a queue name), bypassing bindings entirely.
   */
  send(
    destination: string,
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal?: AbortSignal,
  ): Promise<void>;

  subscribe(
    subscription: TransportSubscription,
    handler: (message: ReceivedMessage) => Promise<void>,
  ): Promise<Subscription>;

  close(): Promise<void>;
}

/** Port of TransportSubscription. `messageTypeNames` drives both queue bindings and dispatch. */
export interface TransportSubscription {
  readonly consumerName: string;
  readonly messageTypeNames: readonly string[];
  readonly queueNameHint: string;
}

/** Port of ReceivedMessage. Headers arrive already normalized to strings (see `normalizeHeaders`). */
export interface ReceivedMessage {
  readonly messageTypeName: string;
  /** Dashed Guid; `EMPTY_CORRELATION_ID` if neither the property nor the header parsed. */
  readonly correlationId: string;
  readonly messageId: string;
  readonly body: Buffer;
  readonly headers: Readonly<Record<string, string>>;
  readonly ack: MessageAckContext;
}

/** Port of IMessageAckContext. */
export interface MessageAckContext {
  ack(): Promise<void>;
  nack(requeue: boolean): Promise<void>;
}

export interface Subscription {
  close(): Promise<void>;
}

/** Thrown when a publish is rejected or cannot be routed. Mirrors MessageTransportPublishException. */
export class MessageTransportPublishError extends Error {
  readonly messageTypeName: string;
  readonly correlationId: string;
  readonly isUnroutable: boolean;

  /**
   * `detail` lets each transport say what actually rejected the publish. Without it the message
   * blames "the broker", which is a false lead in a brokerless transport: @vsaga/transport-http
   * reporting a refused TCP connection as a broker nack sends the reader looking for a RabbitMQ
   * that was never in the picture.
   */
  constructor(
    messageTypeName: string,
    correlationId: string,
    isUnroutable: boolean,
    options?: { cause?: unknown; detail?: string },
  ) {
    const outcome = isUnroutable ? 'returned as unroutable' : 'rejected';
    super(
      options?.detail
        ? `Publish of ${messageTypeName} for correlation id ${correlationId} was ${outcome}: ${options.detail}`
        : `Publish of ${messageTypeName} for correlation id ${correlationId} was ${outcome} by the broker.`,
      options,
    );
    this.name = 'MessageTransportPublishError';
    this.messageTypeName = messageTypeName;
    this.correlationId = correlationId;
    this.isUnroutable = isUnroutable;
  }
}
