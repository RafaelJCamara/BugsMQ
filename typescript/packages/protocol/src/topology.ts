/**
 * Service-topology registration: who consumes which message type, on which queue.
 *
 * This is what makes a service resolve to a *named* node on the dashboard Saga Map instead of an
 * `Unresolved` one. On the .NET side it is not a message at all -- TopologyRecordingTransport
 * observes each SubscribeAsync and writes a row to the `SagaConsumerRegistrations` table via EF Core
 * (dotnet/src/VSaga.Persistence.EFCore/EfCoreServiceTopologyStore.cs). There is no wire protocol for
 * it, which is why the TypeScript side reports over the Dashboard API instead of holding Postgres
 * credentials.
 *
 * Casing note: this DTO crosses the Dashboard REST API, which serializes **camelCase** -- unlike the
 * broker payloads, which are PascalCase (see codec.ts). The lowercase field names below are correct
 * and are not a mistake.
 */
export interface TopologyRegistration {
  readonly serviceName: string;
  readonly messageType: string;
  readonly queueName: string;
}

/**
 * Reports subscriptions so they show up on the Saga Map. Implementations must be best-effort:
 * a cold or unreachable dashboard must never stop a participant from starting, matching the
 * rationale in .NET's AddVSagaTopologyRecording.
 */
export interface TopologyReporter {
  report(registrations: readonly TopologyRegistration[]): Promise<void>;
}
