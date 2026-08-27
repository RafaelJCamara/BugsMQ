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
  const url = new URL(path, baseUrl).toString();

  return {
    async report(registrations: readonly TopologyRegistration[]): Promise<void> {
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
