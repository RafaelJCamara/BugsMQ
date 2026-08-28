import type { TopologyRegistration, TopologyReporter } from '@vsaga/protocol';

export interface HttpTopologyReporterOptions {
  /** Base URL of the vSaga Dashboard API, e.g. `http://dashboard-api:8080`. */
  readonly baseUrl: string;
  readonly apiKey: string;
  readonly timeoutMs?: number;
  readonly path?: string;
}

/**
 * Reports this service's subscriptions to the Dashboard API so it resolves to a named node on the
 * Saga Map instead of an `Unresolved` one.
 *
 * Why HTTP and not the database: on the .NET side topology recording is not a message but a direct
 * EF Core write to `SagaConsumerRegistrations` (EfCoreServiceTopologyStore), so there is no wire
 * protocol to speak. Rather than hand a Node service Postgres credentials and a duplicate copy of
 * the schema, the Dashboard API exposes the same upsert over REST.
 *
 * Casing: the request body is camelCase because the Dashboard API configures camelCase JSON --
 * unlike broker payloads, which are PascalCase. This asymmetry is real and easy to trip over.
 */
export function httpTopologyReporter(options: HttpTopologyReporterOptions): TopologyReporter {
  const { baseUrl, apiKey, timeoutMs = 5000, path = '/api/topology/registrations' } = options;

  return {
    async report(registrations: readonly TopologyRegistration[]): Promise<void> {
      // Built here, not eagerly at httpTopologyReporter(...) call time: a malformed baseUrl (missing
      // scheme, e.g. "localhost:5080" instead of "http://localhost:5080") throws synchronously from
      // `new URL(...)`, and this function is the caller's best-effort try/catch boundary
      // (participant.ts's start()) -- constructing it eagerly would throw before that boundary
      // exists, crashing the whole process over a topology-config mistake instead of degrading to an
      // Unresolved Saga Map node the way every other topology-reporting failure already does.
      const url = new URL(path, baseUrl).toString();
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'content-type': 'application/json', 'x-api-key': apiKey },
        body: JSON.stringify(registrations),
        signal: AbortSignal.timeout(timeoutMs),
      });

      if (!response.ok) {
        throw new Error(
          `Topology registration POST ${url} returned ${response.status} ${response.statusText}.`,
        );
      }
    },
  };
}
