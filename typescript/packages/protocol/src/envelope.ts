import {
  CAUSATION_ID_HEADER,
  CORRELATION_ID_HEADER,
  ENGINE_OWNED_HEADERS,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  SOURCE_SERVICE_HEADER,
} from './headers.js';
import { assertDashedGuid, newMessageId } from './guid.js';

/** Port of MessageEnvelope (dotnet/src/VSaga.Abstractions/Transport/MessageEnvelope.cs). */
export interface MessageEnvelope {
  /** Dashed Guid. */
  readonly correlationId: string;
  /** Undashed 32-hex Guid, fresh per published message. */
  readonly messageId: string;
  readonly headers: Readonly<Record<string, string>>;
}

/** Port of `MessageEnvelope.New`. */
export function newEnvelope(
  correlationId: string,
  headers: Readonly<Record<string, string>> = {},
): MessageEnvelope {
  assertDashedGuid(correlationId, 'correlationId');
  return { correlationId, messageId: newMessageId(), headers };
}

/**
 * Port of `MessageEnvelope.From` -- the shape every participant reply uses.
 *
 * Two details are load-bearing:
 *   - `messageId` is always FRESH. Reusing the inbound id makes the reply vanish: the orchestrator
 *     dedupes on (SagaType, correlationId, messageId) and drops the repeat at debug level, so the
 *     saga just sits until its timeout and looks like a hung participant.
 *   - `causationId` is the INBOUND message's id. It is what draws the edge on the dashboard Saga
 *     Map; the orchestrator itself correlates on `correlationId`.
 */
export function envelopeFrom(
  sourceService: string,
  correlationId: string,
  causationId?: string,
  headers: Readonly<Record<string, string>> = {},
): MessageEnvelope {
  assertDashedGuid(correlationId, 'correlationId');

  const merged: Record<string, string> = { ...headers, [SOURCE_SERVICE_HEADER]: sourceService };
  if (causationId !== undefined) merged[CAUSATION_ID_HEADER] = causationId;

  for (const owned of ENGINE_OWNED_HEADERS) delete merged[owned];

  return { correlationId, messageId: newMessageId(), headers: merged };
}

/**
 * The three reserved headers plus every envelope header, matching
 * RabbitMqTransport.BuildHeaders / HttpMessageTransport.ApplyVSagaHeaders. Envelope headers are
 * applied last so they can't be shadowed by the reserved three -- same ordering as .NET. Nothing
 * here special-cases `traceparent`/`tracestate`: once a caller has put them in `envelope.headers`
 * (see `envelopeFrom`'s callers), they ride along like any other envelope header.
 */
export function buildHeaders(
  envelope: MessageEnvelope,
  messageTypeName: string,
): Record<string, string> {
  return {
    [CORRELATION_ID_HEADER]: envelope.correlationId,
    [MESSAGE_ID_HEADER]: envelope.messageId,
    [MESSAGE_TYPE_HEADER]: messageTypeName,
    ...envelope.headers,
  };
}

/**
 * MessageEnvelope.Headers is an open dictionary, so a value ultimately comes from user code. On the
 * HTTP transport a raw CR/LF would let that value inject a header or smuggle a request, so .NET
 * rejects it outright (HttpMessageTransport.ApplyVSagaHeaders). Same check, same reason.
 */
export function assertHeaderValueSafe(key: string, value: string): void {
  if (value.includes('\r') || value.includes('\n')) {
    throw new TypeError(
      `Header '${key}' contains a CR or LF character, which is not permitted in an HTTP header value.`,
    );
  }
}

export function assertHeadersSafe(headers: Readonly<Record<string, string>>): void {
  for (const [key, value] of Object.entries(headers)) assertHeaderValueSafe(key, value);
}
