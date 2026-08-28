import { AsyncLocalStorage } from 'node:async_hooks';
import type {
  MessageAckContext,
  MessageEnvelope,
  ReceivedMessage,
  Subscription,
  TransportSubscription,
} from '@vsaga/protocol';

/**
 * No broker underneath means no delivery guarantee to ack/nack against -- the in-process dispatch
 * this class does is not durable, and a saga's own state timeout is the safety net, exactly as it
 * already is for a lost broker message. Mirrors NoOpAckContext
 * (dotnet/src/VSaga.Transport.Http/HttpInboundDispatcher.cs).
 */
export const NO_OP_ACK_CONTEXT: MessageAckContext = {
  ack: () => Promise.resolve(),
  nack: () => Promise.resolve(),
};

export interface CapturedReply {
  readonly messageTypeName: string;
  readonly body: Buffer;
  readonly envelope: MessageEnvelope;
}

export interface InlineDispatchResult {
  readonly reply?: CapturedReply;
}

const ACCEPTED: InlineDispatchResult = {};

/**
 * Ambient collector installed by dispatchInline() for the duration of one inline dispatch -- the
 * only seam available to intercept a handler's ordinary publish() call and capture it as that
 * same request's synchronous reply (docs/design/http-based-sagas.md §3.2). Always a fresh instance per
 * request, never shared, and sealed once every subscriber handler's own *awaited* chain has
 * settled, so a publish a handler genuinely awaits (however many ticks it takes) is always seen
 * before sealing.
 *
 * Unlike .NET, this is NOT a hard guarantee against a handler's *detached*, never-awaited publish
 * (`void somePromise.then(() => ctx.publish(...))`): .NET's own equivalent case only reliably
 * falls through to a real publish attempt because `Task.Run` always incurs real thread-pool
 * dispatch latency, not because of an enforced ordering -- nothing stops a sufficiently fast
 * `Task.Run` continuation from winning there either. Node has no such latency for a continuation
 * that's already-queued as a microtask (e.g. `.then()` on an already-resolved promise): it can run
 * -- and empirically does -- before this collector is sealed. Treat "detached publish from inside
 * a handler" as unspecified ordering on both runtimes, not a supported pattern; a handler that
 * wants a publish to definitely NOT be captured as the sync reply must `await` it.
 */
class SyncReplyCollector {
  #sealed = false;
  #captured: CapturedReply | undefined;

  get captured(): CapturedReply | undefined {
    return this.#captured;
  }

  /** True if this call captured `reply` as the reply; false if sealed or already holding one -- the caller must then fall through to a normal (possibly unroutable) publish. */
  tryCapture(reply: CapturedReply): boolean {
    if (this.#sealed || this.#captured !== undefined) return false;
    this.#captured = reply;
    return true;
  }

  seal(): void {
    this.#sealed = true;
  }
}

const syncReplyCollectorStorage = new AsyncLocalStorage<SyncReplyCollector | undefined>();

/** The ambient collector for the in-flight inline dispatch on this async context, if any. */
export function currentSyncReplyCollector(): Pick<SyncReplyCollector, 'tryCapture'> | undefined {
  return syncReplyCollectorStorage.getStore();
}

interface SubscriberEntry {
  readonly subscription: TransportSubscription;
  readonly handler: (message: ReceivedMessage) => Promise<void>;
}

/**
 * Bound on acquiring the correlation gate for a genuine inbound request before giving up and
 * deferring to a background dispatch instead of continuing to block the HTTP connection. Found
 * live (docs/design/http-based-sagas.md §4.4a): a fan-out reply that routes back to its own originating
 * service can deadlock that service's own gate against itself -- the saga's dispatch holds the
 * gate while awaiting a step's HTTP response, and the participant handling that step can't finish
 * answering until its own nested reply back to the saga host is accepted, which needs the very
 * gate the saga is still holding. Deferring after a short bound breaks the cycle losslessly (202
 * now, dispatched once the gate frees) instead of blocking for the full request timeout.
 */
const INLINE_GATE_ACQUIRE_TIMEOUT_MS = 5000;

/**
 * A per-correlation-id mutex. Node has no threads, so unlike .NET's SemaphoreSlim this exists
 * purely to serialize *interleaving* across await points, not true concurrency -- but the
 * correctness property (two dispatches for the same correlation id never run overlapping) is the
 * same one docs/design/http-based-sagas.md §3.1 needs.
 */
class AsyncGate {
  #locked = false;
  #waiters: Array<() => void> = [];

  /** Resolves true once acquired, or false if `timeoutMs` elapses first without acquiring. */
  async acquire(timeoutMs?: number): Promise<boolean> {
    if (!this.#locked) {
      this.#locked = true;
      return true;
    }

    return new Promise<boolean>((resolve) => {
      let settled = false;
      let timer: ReturnType<typeof setTimeout> | undefined;

      const onAcquired = (): void => {
        if (settled) return;
        settled = true;
        if (timer !== undefined) clearTimeout(timer);
        resolve(true);
      };

      if (timeoutMs !== undefined) {
        timer = setTimeout(() => {
          if (settled) return;
          settled = true;
          const index = this.#waiters.indexOf(onAcquired);
          if (index >= 0) this.#waiters.splice(index, 1);
          resolve(false);
        }, timeoutMs);
      }

      this.#waiters.push(onAcquired);
    });
  }

  /** Hands the lock straight to the next waiter, if any, or marks the gate free. */
  release(): void {
    const next = this.#waiters.shift();
    if (next) {
      next();
      return;
    }
    this.#locked = false;
  }

  /** True once nobody holds or is waiting on this gate -- an idle gate has no waiter to strand if dropped. */
  get isIdle(): boolean {
    return !this.#locked && this.#waiters.length === 0;
  }
}

/**
 * Owns the local subscriber registry (populated by subscribe()) and the per-correlation-id
 * dispatch gate that is this adapter's whole answer to docs/design/http-based-sagas.md §3.1: a reply
 * must never re-enter a saga while its own step is still running.
 *
 * Exactly two entry points ever reach a local subscriber, and the asymmetry between them is the
 * entire §3.1 answer:
 *   - dispatchInline() -- a genuine inbound HTTP request. Dispatched immediately, holding the
 *     gate, because the handler's reply has to be captured before the response is written --
 *     unless the gate can't be acquired within INLINE_GATE_ACQUIRE_TIMEOUT_MS, in which case it
 *     falls back to enqueueLocalDispatch() instead of blocking the connection for the full
 *     request timeout.
 *   - enqueueLocalDispatch() -- everything else that resolves to a local subscriber: a
 *     same-process publish/send (including redelivery of an inbound type, which runs from
 *     *inside* an already-gated dispatch) and a 200 reply to our own outbound POST. Never
 *     dispatched inline -- always scheduled as its own task that acquires the same gate, which is
 *     what lets redelivery enqueue itself without deadlocking on the gate its own caller may
 *     already hold, and what makes a reply wait for the publishing step to finish before it can be
 *     dispatched.
 *
 * Unlike the .NET dispatcher there is no explicit Channel + pump loop: enqueueLocalDispatch kicks
 * off its own async task directly. That task runs synchronously up to its first await (acquiring
 * the gate), so calls made in enqueue order register on the gate's waiter queue in that same
 * order -- Node's run-to-first-await semantics give the same FIFO-per-correlation ordering the
 * .NET Channel provides, without needing a queue data structure to get it.
 */
export class HttpInboundDispatcher {
  readonly #subscribers = new Map<string, SubscriberEntry>();
  readonly #correlationGates = new Map<string, AsyncGate>();
  #nextSubscriberId = 0;

  subscribe(
    subscription: TransportSubscription,
    handler: (message: ReceivedMessage) => Promise<void>,
  ): Subscription {
    const id = String(this.#nextSubscriberId++);
    this.#subscribers.set(id, { subscription, handler });
    return {
      close: () => {
        this.#subscribers.delete(id);
        return Promise.resolve();
      },
    };
  }

  /** Whether any locally-registered subscription declares this message type -- the local half of the routing union (docs/design/http-based-sagas.md §3.3a). */
  hasLocalSubscriber(messageTypeName: string): boolean {
    for (const entry of this.#subscribers.values()) {
      if (entry.subscription.messageTypeNames.includes(messageTypeName)) return true;
    }
    return false;
  }

  /**
   * Queues a message for local dispatch without blocking on the correlation gate -- see the class
   * doc for why this, never inline, is the only path other than a genuine inbound request.
   *
   * Explicitly detached from whatever ambient sync-reply collector is active at the call site by
   * running under an `undefined` AsyncLocalStorage store. This call can happen from *inside* an
   * active dispatchInline() (a same-process publish from a handler, or redelivery running from
   * that handler's own catch block), and without this the newly-spawned task would inherit that
   * unrelated request's collector via ordinary AsyncLocalStorage propagation -- letting this
   * dispatch's own eventual unroutable publish get hijacked as if it were THAT other, unrelated
   * request's synchronous reply. .NET can't have this problem structurally: HttpInboundDispatcher's
   * pump is one persistent Task started once at construction, long before any request-scoped
   * AsyncLocal exists, so a pump-driven dispatch always observes a null collector. Running with an
   * explicit `undefined` store here reproduces that same guarantee.
   */
  enqueueLocalDispatch(received: ReceivedMessage): void {
    void syncReplyCollectorStorage.run(undefined, () => this.#dispatchAndDrop(received));
  }

  /** The one inline path: a genuine inbound HTTP request, dispatched immediately under an ambient reply collector so a synchronous reply can be captured before the caller's response is written. */
  async dispatchInline(received: ReceivedMessage): Promise<InlineDispatchResult> {
    const gate = this.#gateFor(received.correlationId);
    const acquired = await gate.acquire(INLINE_GATE_ACQUIRE_TIMEOUT_MS);

    if (!acquired) {
      this.enqueueLocalDispatch(received);
      return ACCEPTED;
    }

    const collector = new SyncReplyCollector();
    try {
      await syncReplyCollectorStorage.run(collector, () => this.#runSubscribers(received));
    } finally {
      collector.seal();
      this.#releaseGate(received.correlationId, gate);
    }

    return collector.captured ? { reply: collector.captured } : ACCEPTED;
  }

  async #dispatchAndDrop(received: ReceivedMessage): Promise<void> {
    const gate = this.#gateFor(received.correlationId);
    await gate.acquire();
    try {
      await this.#runSubscribers(received);
    } finally {
      this.#releaseGate(received.correlationId, gate);
    }
  }

  /**
   * Invokes every matching subscriber's handler in turn, each independently caught and dropped so
   * one failing subscriber can't take down a sibling's fan-out delivery of the same message --
   * mirroring RabbitMqTransport's own dispatch-level catch (log + drop rather than propagate,
   * `@vsaga/transport-rabbitmq`'s `#dispatch`). Assumes the caller already holds this
   * correlation's gate.
   *
   * Snapshots the subscriber list up front rather than iterating the live Map: close() clearing
   * `#subscribers` while this loop is paused at an `await` would otherwise silently truncate a
   * still-in-flight multi-subscriber fan-out (Map iteration ends early once the Map it's iterating
   * is cleared), leaving a later subscriber's handler never invoked despite the dispatch appearing
   * to complete normally to its caller.
   */
  async #runSubscribers(received: ReceivedMessage): Promise<void> {
    for (const entry of [...this.#subscribers.values()]) {
      if (!entry.subscription.messageTypeNames.includes(received.messageTypeName)) continue;

      try {
        await entry.handler(received);
      } catch {
        // Swallowed on purpose -- see the method doc.
      }
    }
  }

  #gateFor(correlationId: string): AsyncGate {
    let gate = this.#correlationGates.get(correlationId);
    if (!gate) {
      gate = new AsyncGate();
      this.#correlationGates.set(correlationId, gate);
    }
    return gate;
  }

  /** Best-effort cleanup: only removes the entry if it's uncontended right now, which is safe either way -- an idle gate has no waiter to strand. */
  #releaseGate(correlationId: string, gate: AsyncGate): void {
    gate.release();
    if (gate.isIdle) this.#correlationGates.delete(correlationId);
  }

  /** Drops every locally-registered subscriber. A dispatch already in flight still runs to completion; nothing new is handed to a removed subscriber afterward. */
  close(): void {
    this.#subscribers.clear();
  }
}
