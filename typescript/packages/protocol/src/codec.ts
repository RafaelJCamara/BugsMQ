/**
 * Message body codec.
 *
 * .NET publishes with `JsonSerializer.SerializeToUtf8Bytes(message, messageType)` using
 * System.Text.Json DEFAULTS (dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs). No naming
 * policy, no `$type` discriminator, no envelope wrapper -- so the JSON keys are the C# property
 * names verbatim, i.e. **PascalCase**, and the TypeScript interface *is* the JSON shape. There is no
 * mapping layer here on purpose.
 *
 * The trap: the Dashboard REST API serializes **camelCase** (its Program.cs configures
 * `ConfigureHttpJsonOptions`), and typescript/dashboard-web/src/app/models/saga.model.ts mirrors
 * that convention. Those models are the natural thing to copy from and they are the wrong shape for
 * the broker. Do not conflate the two.
 */

export function encodeBody(body: unknown): Buffer {
  return Buffer.from(JSON.stringify(body), 'utf8');
}

export function decodeBody<TBody>(body: Uint8Array | Buffer | string): TBody {
  const text = typeof body === 'string' ? body : Buffer.from(body).toString('utf8');
  return JSON.parse(text) as TBody;
}

/**
 * amqplib hands back AMQP `longstr` header values as `Buffer`, not `string`, so every header read
 * must normalize or comparisons silently fail. The .NET side already normalizes the reverse
 * direction (RabbitMqTransport.GetHeaderString handles byte[]/string/other), which means publishing
 * plain JS strings is safe -- the hazard is one-way, on read.
 */
export function normalizeHeaders(
  headers: Record<string, unknown> | undefined | null,
): Record<string, string> {
  const result: Record<string, string> = {};
  if (!headers) return result;

  for (const [key, value] of Object.entries(headers)) {
    if (value === null || value === undefined) continue;
    result[key] = Buffer.isBuffer(value) ? value.toString('utf8') : String(value);
  }

  return result;
}
