import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubService } from '../../services/saga-hub.service';
import { SagaDetail as SagaDetailModel, SagaLogEntry, SagaMap as SagaMapModel, SagaStatus, SagaSummary } from '../../models/saga.model';
import { SagaDetail } from './saga-detail';

function makeDetail(overrides: Partial<SagaSummary> = {}): SagaDetailModel {
  return {
    summary: {
      correlationId: 'saga-1',
      sagaType: 'OrderSaga',
      kind: 'Orchestrated',
      currentState: 'Failed',
      status: 'Failed',
      createdAtUtc: '2026-01-01T00:00:00Z',
      updatedAtUtc: '2026-01-01T00:00:01Z',
      version: 2,
      ...overrides,
    },
    dataJson: null,
  };
}

function makeMap(overrides: Partial<SagaMapModel> = {}): SagaMapModel {
  return {
    summary: makeDetail().summary,
    nodes: [],
    edges: [],
    events: [],
    failureEventIndex: null,
    ...overrides,
  };
}

function makeEntry(overrides: Partial<SagaLogEntry> = {}): SagaLogEntry {
  return {
    sequenceNumber: 1,
    correlationId: 'saga-1',
    sagaType: 'OrderSaga',
    entryType: 'SagaStarted',
    fromState: null,
    toState: 'Submitted',
    messageType: 'OrderSubmitted',
    messageId: 'm0',
    payloadJson: null,
    errorMessage: null,
    traceId: null,
    spanId: null,
    occurredAtUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('SagaDetail', () => {
  let apiMock: {
    get: ReturnType<typeof vi.fn>;
    getTimeline: ReturnType<typeof vi.fn>;
    getMap: ReturnType<typeof vi.fn>;
    retry: ReturnType<typeof vi.fn>;
  };
  let hubMock: {
    sagaUpdated$: Subject<SagaSummary>;
    timelineEntryAdded$: Subject<{ correlationId: string; entry: SagaLogEntry }>;
    subscribeToSaga: ReturnType<typeof vi.fn>;
    unsubscribeFromSaga: ReturnType<typeof vi.fn>;
  };

  function setup(detail: SagaDetailModel = makeDetail(), timeline: SagaLogEntry[] = [], map: SagaMapModel = makeMap()) {
    apiMock = {
      get: vi.fn().mockReturnValue(of(detail)),
      getTimeline: vi.fn().mockReturnValue(of(timeline)),
      getMap: vi.fn().mockReturnValue(of(map)),
      retry: vi.fn(),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      timelineEntryAdded$: new Subject<{ correlationId: string; entry: SagaLogEntry }>(),
      subscribeToSaga: vi.fn().mockResolvedValue(undefined),
      unsubscribeFromSaga: vi.fn().mockResolvedValue(undefined),
    };

    TestBed.configureTestingModule({
      imports: [SagaDetail],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: { get: () => 'saga-1' } } },
        },
      ],
    });

    const fixture = TestBed.createComponent(SagaDetail);
    fixture.detectChanges();
    return fixture;
  }

  it('loads the saga detail and timeline for the routed id, and subscribes on the hub', () => {
    const detail = makeDetail();
    const entries = [makeEntry()];
    const fixture = setup(detail, entries);

    expect(apiMock.get).toHaveBeenCalledWith('saga-1');
    expect(apiMock.getTimeline).toHaveBeenCalledWith('saga-1');
    expect(hubMock.subscribeToSaga).toHaveBeenCalledWith('saga-1');
    expect(fixture.componentInstance.detail()).toEqual(detail);
    expect(fixture.componentInstance.timeline()).toEqual(entries);
    expect(fixture.componentInstance.loading()).toBe(false);
  });

  it('shows an error state when the detail request fails', () => {
    const fixture = setup();
    apiMock.get.mockReturnValue(throwError(() => new Error('404')));

    fixture.componentInstance.load();

    expect(fixture.componentInstance.error()).toContain('Could not load');
    expect(fixture.componentInstance.loading()).toBe(false);
  });

  it('unsubscribes from the hub on destroy', () => {
    const fixture = setup();
    fixture.destroy();
    expect(hubMock.unsubscribeFromSaga).toHaveBeenCalledWith('saga-1');
  });

  it.each<SagaStatus>(['Failed', 'TimedOut'])('shows the retry button when status is %s', (status) => {
    const fixture = setup(makeDetail({ status }));
    const button = fixture.nativeElement.querySelector('.retry-row button');
    expect(button).not.toBeNull();
  });

  it.each<SagaStatus>(['Running', 'Completed', 'Compensating', 'Compensated', 'Cancelled'])(
    'hides the retry button when status is %s',
    (status) => {
      const fixture = setup(makeDetail({ status }));
      const button = fixture.nativeElement.querySelector('.retry-row button');
      expect(button).toBeNull();
    },
  );

  it('retry() calls the API and shows a success message', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(of(undefined));

    fixture.componentInstance.retry();

    expect(apiMock.retry).toHaveBeenCalledWith('saga-1');
    expect(fixture.componentInstance.retrying()).toBe(false);
    expect(fixture.componentInstance.retryMessage()).toContain('Retry accepted');
  });

  it('retry() surfaces the server error message on failure', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(throwError(() => ({ error: { error: 'Saga cannot be retried' } })));

    fixture.componentInstance.retry();

    expect(fixture.componentInstance.retrying()).toBe(false);
    expect(fixture.componentInstance.retryMessage()).toBe('Saga cannot be retried');
  });

  it('retry() falls back to a generic message when the server gives no error detail', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    apiMock.retry.mockReturnValue(throwError(() => ({})));

    fixture.componentInstance.retry();

    expect(fixture.componentInstance.retryMessage()).toBe('Retry failed.');
  });

  it('prettyDataJson pretty-prints valid JSON', () => {
    const fixture = setup(makeDetail());
    fixture.componentInstance.detail.set({ ...fixture.componentInstance.detail()!, dataJson: '{"a":1}' });

    expect(fixture.componentInstance.prettyDataJson).toBe(JSON.stringify({ a: 1 }, null, 2));
  });

  it('prettyDataJson returns the raw string when the JSON is invalid', () => {
    const fixture = setup(makeDetail());
    fixture.componentInstance.detail.set({ ...fixture.componentInstance.detail()!, dataJson: 'not json' });

    expect(fixture.componentInstance.prettyDataJson).toBe('not json');
  });

  it('prettyDataJson returns an empty string when there is no data', () => {
    const fixture = setup(makeDetail());
    expect(fixture.componentInstance.prettyDataJson).toBe('');
  });

  it('setTab switches the active tab', () => {
    const fixture = setup();
    expect(fixture.componentInstance.tab()).toBe('map'); // Map is the default tab

    fixture.componentInstance.setTab('data');

    expect(fixture.componentInstance.tab()).toBe('data');
  });

  it('applies a live saga update when the correlation id matches', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    const updated: SagaSummary = { ...fixture.componentInstance.detail()!.summary, status: 'Completed' };

    hubMock.sagaUpdated$.next(updated);

    expect(fixture.componentInstance.detail()!.summary.status).toBe('Completed');
  });

  it('ignores a live saga update for a different correlation id', () => {
    const fixture = setup(makeDetail({ status: 'Failed' }));
    const other: SagaSummary = { ...fixture.componentInstance.detail()!.summary, correlationId: 'other-id', status: 'Completed' };

    hubMock.sagaUpdated$.next(other);

    expect(fixture.componentInstance.detail()!.summary.status).toBe('Failed');
  });

  it('appends a live timeline entry when the correlation id matches', () => {
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2, entryType: 'StepSucceeded' });

    hubMock.timelineEntryAdded$.next({ correlationId: 'saga-1', entry });

    expect(fixture.componentInstance.timeline()).toContainEqual(entry);
  });

  it('ignores a live timeline entry for a different correlation id', () => {
    const fixture = setup();
    const entry = makeEntry({ sequenceNumber: 2 });

    hubMock.timelineEntryAdded$.next({ correlationId: 'other-id', entry });

    expect(fixture.componentInstance.timeline()).toEqual([]);
  });

  it('loads the map alongside the timeline', () => {
    const map = makeMap();
    const fixture = setup(makeDetail(), [], map);

    expect(apiMock.getMap).toHaveBeenCalledWith('saga-1');
    expect(fixture.componentInstance.map()).toEqual(map);
  });

  it('re-fetches the map (not incrementally, via a whole re-fetch) when a live saga update arrives', () => {
    const fixture = setup();
    expect(apiMock.getMap).toHaveBeenCalledTimes(1);

    hubMock.sagaUpdated$.next(fixture.componentInstance.detail()!.summary);

    expect(apiMock.getMap).toHaveBeenCalledTimes(2);
  });

  it('does not re-fetch the map for a live saga update on a different correlation id', () => {
    const fixture = setup();
    const other: SagaSummary = { ...fixture.componentInstance.detail()!.summary, correlationId: 'other-id' };

    hubMock.sagaUpdated$.next(other);

    expect(apiMock.getMap).toHaveBeenCalledTimes(1);
  });

  it('switching to the Map tab renders the saga-map component once the map has loaded', () => {
    const fixture = setup();
    fixture.componentInstance.setTab('map');
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('app-saga-map')).not.toBeNull();
  });
});
