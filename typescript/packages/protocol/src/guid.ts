import { randomUUID } from 'node:crypto';

/**
 * vSaga puts two *different* Guid formats in one envelope, and mixing them up is a silent failure:
 *
 *   - `correlationId` is .NET's "D" format -- dashed, lowercase (RabbitMqTransport stamps
 *     `envelope.CorrelationId.ToString()`).
 *   - `messageId` is .NET's "N" format -- 32 hex chars, NO dashes (MessageEnvelope.New/From use
 *     `Guid.NewGuid().ToString("N")`).
 *
 * Callers should never hand-format a Guid; use these helpers.
 */

const DASHED = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const UNDASHED = /^[0-9a-f]{32}$/i;

/** A fresh dashed Guid, matching .NET's `Guid.NewGuid().ToString()`. */
export function newCorrelationId(): string {
  return randomUUID();
}

/** A fresh undashed 32-hex Guid, matching .NET's `Guid.NewGuid().ToString("N")`. */
export function newMessageId(): string {
  return randomUUID().replace(/-/g, '');
}

export function isDashedGuid(value: string): boolean {
  return DASHED.test(value);
}

export function isUndashedGuid(value: string): boolean {
  return UNDASHED.test(value);
}

/**
 * The all-zero correlation id. RabbitMqTransport falls back to `Guid.Empty` when neither the AMQP
 * property nor the header parses; the orchestrator then fails `CanInitiate` and logs the message as
 * an UnexpectedEvent rather than throwing -- so this value showing up is the fingerprint of a
 * correlation id that was dropped somewhere upstream, not a legitimate id.
 */
export const EMPTY_CORRELATION_ID = '00000000-0000-0000-0000-000000000000';

export function assertDashedGuid(value: string, field: string): void {
  if (!DASHED.test(value)) {
    throw new TypeError(
      `${field} must be a dashed lowercase Guid (e.g. "3f2504e0-4f89-11d3-9a0c-0305e82c3301"), got ${JSON.stringify(value)}.`,
    );
  }
}
