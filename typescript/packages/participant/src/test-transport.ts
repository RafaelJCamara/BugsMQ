import type {
  MessageEnvelope,
  MessageTransport,
  ReceivedMessage,
  Subscription,
  TransportSubscription,
} from '@vsaga/protocol';

/**
 * A recording in-memory MessageTransport for unit tests.
 *
 * Test-only on purpose: it is not exported from the package index and is not published. The shipped
 * transport is @vsaga/transport-rabbitmq. This exists so handler behaviour (dispatch, dedupe, ack
 * semantics, causation stamping) can be tested without Docker.
 */
export interface PublishedMessage {
  readonly messageTypeName: string;
  readonly body: Buffer;
  readonly envelope: MessageEnvelope;
  readonly destination?: string;
}

export class FakeTransport implements MessageTransport {
  readonly published: PublishedMessage[] = [];
  readonly acked: string[] = [];
  readonly nacked: { messageId: string; requeue: boolean }[] = [];
  subscription?: TransportSubscription;
  closed = false;

  // Explicit `| undefined` rather than `?:` -- exactOptionalPropertyTypes forbids assigning
  // undefined back to an optional property, and close() needs to clear this.
  #handler: ((message: ReceivedMessage) => Promise<void>) | undefined;

  publish(messageTypeName: string, body: Buffer, envelope: MessageEnvelope): Promise<void> {
    this.published.push({ messageTypeName, body, envelope });
    return Promise.resolve();
  }

  send(
    destination: string,
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
  ): Promise<void> {
    this.published.push({ messageTypeName, body, envelope, destination });
    return Promise.resolve();
  }

  subscribe(
    subscription: TransportSubscription,
    handler: (message: ReceivedMessage) => Promise<void>,
  ): Promise<Subscription> {
    this.subscription = subscription;
    this.#handler = handler;
    return Promise.resolve({
      close: () => {
        this.#handler = undefined;
        return Promise.resolve();
      },
    });
  }

  close(): Promise<void> {
    this.closed = true;
    return Promise.resolve();
  }

  /** Drives one inbound delivery through the participant, as the broker would. */
  async deliver(message: {
    messageTypeName: string;
    correlationId: string;
    messageId: string;
    body: unknown;
    headers?: Record<string, string>;
  }): Promise<void> {
    if (!this.#handler) throw new Error('Nothing is subscribed.');

    await this.#handler({
      messageTypeName: message.messageTypeName,
      correlationId: message.correlationId,
      messageId: message.messageId,
      body: Buffer.from(JSON.stringify(message.body), 'utf8'),
      headers: message.headers ?? {},
      ack: {
        ack: () => {
          this.acked.push(message.messageId);
          return Promise.resolve();
        },
        nack: (requeue: boolean) => {
          this.nacked.push({ messageId: message.messageId, requeue });
          return Promise.resolve();
        },
      },
    });
  }
}
