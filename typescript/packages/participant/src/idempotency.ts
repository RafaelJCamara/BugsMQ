/**
 * Bounds a duplicated delivery -- vSaga.Chaos's `Duplicate` fault, or a genuine broker
 * at-least-once redelivery -- from running a participant's business side effect twice.
 */
export interface IdempotencyStore {
  /** True (and records it) the first time this message id is seen; false for a repeat. */
  tryClaim(messageId: string): Promise<boolean> | boolean;
}

/**
 * Port of ParticipantService's dedupe
 * (dotnet/samples/VSaga.Samples.OrderProcessing/Participants/ParticipantService.cs).
 *
 * Deliberately the same design as .NET's, including the same 4096 bound: process-local and
 * capacity-bounded rather than durable or TTL-based. That is good enough to absorb the kind of
 * near-immediate redelivery chaos testing injects, and it is explicitly NOT a substitute for a real
 * idempotency store in a production participant -- it does not survive a restart and it does not
 * coordinate across replicas. Swap in your own IdempotencyStore when you need those.
 *
 * A Map preserves insertion order, so eviction is just the first key.
 */
export class InMemoryIdempotencyStore implements IdempotencyStore {
  readonly #seen = new Set<string>();
  readonly #max: number;

  constructor(maxTrackedMessageIds = 4096) {
    this.#max = maxTrackedMessageIds;
  }

  tryClaim(messageId: string): boolean {
    if (this.#seen.has(messageId)) return false;

    this.#seen.add(messageId);

    while (this.#seen.size > this.#max) {
      const oldest = this.#seen.values().next();
      if (oldest.done) break;
      this.#seen.delete(oldest.value);
    }

    return true;
  }

  get size(): number {
    return this.#seen.size;
  }
}
