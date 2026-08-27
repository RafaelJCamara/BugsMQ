/** Mirrors HttpTransportOptions (dotnet/src/VSaga.Transport.Http/HttpTransportOptions.cs). */
export interface HttpTransportOptions {
  /**
   * This process's own identity, for diagnostics only -- never stamped onto envelopes (that's
   * `envelopeFrom`'s job via `x-vsaga-source-service`, `@vsaga/protocol`).
   */
  readonly serviceName?: string;
  /** Endpoint name -> base URL, e.g. `{ payments: 'http://payments:8080' }`. */
  readonly endpoints?: Readonly<Record<string, string>>;
  /**
   * Message type name -> endpoint names to POST to on publish(). A `"*"` key is a wildcard
   * fallback used for any type with no explicit entry -- e.g. a dashboard/ops process that only
   * ever redrives messages toward a single saga host and has no reason to enumerate every message
   * type that host understands.
   */
  readonly routes?: Readonly<Record<string, readonly string[]>>;
  /** Per-request timeout for the outbound HTTP call, including the participant's own processing time. */
  readonly requestTimeoutMs?: number;
  /** Path this service's own receive endpoint is mapped to by a hosting adapter (`@vsaga/express`, `@vsaga/fastify`, `@vsaga/nestjs`, ...). */
  readonly inboundPath?: string;
}

export interface ResolvedHttpTransportOptions {
  readonly serviceName: string;
  readonly endpoints: Readonly<Record<string, string>>;
  readonly routes: Readonly<Record<string, readonly string[]>>;
  readonly requestTimeoutMs: number;
  readonly inboundPath: string;
}

export function resolveOptions(options: HttpTransportOptions = {}): ResolvedHttpTransportOptions {
  return {
    serviceName: options.serviceName ?? 'vsaga-http',
    endpoints: options.endpoints ?? {},
    routes: options.routes ?? {},
    requestTimeoutMs: options.requestTimeoutMs ?? 30_000,
    inboundPath: options.inboundPath ?? '/vsaga/messages',
  };
}
