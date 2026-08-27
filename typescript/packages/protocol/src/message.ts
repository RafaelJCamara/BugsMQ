declare const bodyBrand: unique symbol;

/**
 * A declared vSaga message type: the C# short type name, carrying its body shape as a phantom type.
 *
 * One string is the single source of truth for both `x-vsaga-message-type` and the routing key
 * (which is `toRoutingKey` of it). That is the point of this indirection -- the class of bug where
 * the header says `OrderShipped` but the message went out on the key `ordershipped` becomes
 * unrepresentable.
 */
export interface MessageType<TBody> {
  readonly name: string;
  readonly [bodyBrand]?: TBody;
}

/**
 * Declares a message type. The name must be the **C# short type name** exactly -- `OrderShipped`,
 * not `orderShipped` and not the routing key `order-shipped`.
 *
 * The orchestrator resolves inbound messages by looking `x-vsaga-message-type` up in a dictionary
 * keyed by `Type.Name` (dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs). RabbitMqTransport falls
 * back to the routing key when the header is missing, but that fallback yields `order-shipped`,
 * which is never a key in that dictionary -- so it only turns a missing header into an
 * `UnexpectedEvent` log line, never into a working dispatch. Treat it as a bug detector.
 *
 * @example
 * interface OrderShippedBody { CorrelationId: string; OrderId: string; TrackingNumber: string }
 * export const OrderShipped = message<OrderShippedBody>('OrderShipped');
 */
export function message<TBody>(name: string): MessageType<TBody> {
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) {
    throw new TypeError(
      `Message type name ${JSON.stringify(name)} is not a valid C# short type name. ` +
        `Use the CLR type name (e.g. "OrderShipped"), not a routing key.`,
    );
  }

  return { name };
}

/** The body type carried by a MessageType, for callers that need to name it. */
export type BodyOf<T> = T extends MessageType<infer B> ? B : never;
