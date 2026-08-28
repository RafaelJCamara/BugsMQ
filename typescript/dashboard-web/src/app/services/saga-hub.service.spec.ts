import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { DASHBOARD_API_KEY, HUB_URL } from '../api-config';
import { SagaLogEntry, SagaSummary } from '../models/saga.model';
import { SagaHubService } from './saga-hub.service';

/**
 * A stand-in for signalR.HubConnection: records the server methods invoked on it, exposes the
 * handlers the service registered so a test can push a server-initiated message, and lets each test
 * decide whether start() succeeds and what state the connection reports.
 */
class FakeHubConnection {
  readonly handlers = new Map<string, (...args: unknown[]) => void>();
  readonly invocations: unknown[][] = [];
  startCount = 0;
  stopCount = 0;
  state = 'Disconnected';
  startResult: () => Promise<void> = () => Promise.resolve();

  on(methodName: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(methodName, handler);
  }

  start(): Promise<void> {
    this.startCount += 1;
    return this.startResult();
  }

  invoke(...args: unknown[]): Promise<void> {
    this.invocations.push(args);
    return Promise.resolve();
  }

  stop(): Promise<void> {
    this.stopCount += 1;
    return Promise.resolve();
  }

  /** Drives a handler the service registered via `.on(...)`, as the hub would. */
  emit(methodName: string, ...args: unknown[]): void {
    const handler = this.handlers.get(methodName);
    if (!handler) throw new Error(`No handler registered for '${methodName}'.`);
    handler(...args);
  }
}

/** What HubConnectionBuilder was asked to build, so the URL and access-token wiring can be asserted. */
const built: { url?: string; options?: { accessTokenFactory?: () => string }; automaticReconnect: boolean } = {
  automaticReconnect: false,
};

let connection: FakeHubConnection;

vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected' },
  HubConnectionBuilder: class {
    withUrl(url: string, options?: { accessTokenFactory?: () => string }) {
      built.url = url;
      built.options = options;
      return this;
    }
    withAutomaticReconnect() {
      built.automaticReconnect = true;
      return this;
    }
    build() {
      return connection;
    }
  },
}));

describe('SagaHubService', () => {
  let service: SagaHubService;

  beforeEach(() => {
    connection = new FakeHubConnection();
    built.url = undefined;
    built.options = undefined;
    built.automaticReconnect = false;

    TestBed.configureTestingModule({});
    service = TestBed.inject(SagaHubService);
  });

  it('builds the connection against HUB_URL, with the dashboard api key as the access token', async () => {
    await service.subscribeToList();

    expect(built.url).toBe(HUB_URL);
    expect(built.options?.accessTokenFactory?.()).toBe(DASHBOARD_API_KEY);
  });

  // Without this the browser drops to polling the REST API after any hub blip, and the live
  // updates the dashboard is built around silently stop.
  it('enables automatic reconnect', async () => {
    await service.subscribeToList();

    expect(built.automaticReconnect).toBe(true);
  });

  it('subscribeToList() starts the connection and invokes SubscribeToList', async () => {
    await service.subscribeToList();

    expect(connection.startCount).toBe(1);
    expect(connection.invocations).toEqual([['SubscribeToList']]);
  });

  // Hub groups are keyed by (sagaType, correlationId) server-side: two saga types can share a
  // correlation id, so dropping either argument would subscribe a detail view to another saga's feed.
  it('subscribeToSaga() invokes SubscribeToSaga with both the saga type and the correlation id', async () => {
    await service.subscribeToSaga('OrderSaga', 'abc-123');

    expect(connection.invocations).toEqual([['SubscribeToSaga', 'OrderSaga', 'abc-123']]);
  });

  it('starts the connection once across repeated subscriptions', async () => {
    await service.subscribeToList();
    await service.subscribeToSaga('OrderSaga', 'abc-123');
    await service.subscribeToSaga('OrderSaga', 'def-456');

    expect(connection.startCount).toBe(1);
    expect(connection.invocations).toHaveLength(3);
  });

  // The failed start() promise is cached too, so without clearing it a single failure -- the API not
  // up yet when the tab loads -- would leave the dashboard permanently on the stale first error.
  it('clears the cached start promise on failure, so a later subscription retries', async () => {
    connection.startResult = () => Promise.reject(new Error('hub is down'));

    await expect(service.subscribeToList()).rejects.toThrow('hub is down');
    expect(connection.startCount).toBe(1);

    connection.startResult = () => Promise.resolve();
    await service.subscribeToList();

    expect(connection.startCount).toBe(2);
    expect(connection.invocations).toEqual([['SubscribeToList']]);
  });

  it('pushes a SagaUpdated message onto sagaUpdated$', async () => {
    await service.subscribeToList();

    const summary = { correlationId: 'abc-123', sagaType: 'OrderSaga' } as SagaSummary;
    const seen: SagaSummary[] = [];
    service.sagaUpdated$.subscribe((s) => seen.push(s));

    connection.emit('SagaUpdated', summary);

    expect(seen).toEqual([summary]);
  });

  it('pushes a TimelineEntryAdded message onto timelineEntryAdded$ with its saga type and correlation id', async () => {
    await service.subscribeToList();

    const entry = { sequenceNumber: 7, correlationId: 'abc-123' } as SagaLogEntry;
    const seen: { sagaType: string; correlationId: string; entry: SagaLogEntry }[] = [];
    service.timelineEntryAdded$.subscribe((e) => seen.push(e));

    connection.emit('TimelineEntryAdded', 'OrderSaga', 'abc-123', entry);

    expect(seen).toEqual([{ sagaType: 'OrderSaga', correlationId: 'abc-123', entry }]);
  });

  it('unsubscribeFromSaga() invokes UnsubscribeFromSaga while the connection is up', async () => {
    await service.subscribeToList();
    connection.state = 'Connected';
    connection.invocations.length = 0;

    await service.unsubscribeFromSaga('OrderSaga', 'abc-123');

    expect(connection.invocations).toEqual([['UnsubscribeFromSaga', 'OrderSaga', 'abc-123']]);
  });

  // Navigating away during a reconnect would otherwise invoke on a connection that cannot carry it,
  // turning a routine teardown into an unhandled rejection in the component that triggered it.
  it('unsubscribeFromSaga() is a no-op when the connection is not connected', async () => {
    await service.subscribeToList();
    connection.state = 'Disconnected';
    connection.invocations.length = 0;

    await expect(service.unsubscribeFromSaga('OrderSaga', 'abc-123')).resolves.toBeUndefined();
    expect(connection.invocations).toEqual([]);
  });

  it('unsubscribeFromSaga() is a no-op before any connection exists', async () => {
    await expect(service.unsubscribeFromSaga('OrderSaga', 'abc-123')).resolves.toBeUndefined();
    expect(connection.startCount).toBe(0);
  });

  it('ngOnDestroy() stops the connection', async () => {
    await service.subscribeToList();

    service.ngOnDestroy();

    expect(connection.stopCount).toBe(1);
  });

  it('ngOnDestroy() does nothing when no connection was ever built', () => {
    expect(() => service.ngOnDestroy()).not.toThrow();
    expect(connection.stopCount).toBe(0);
  });
});
