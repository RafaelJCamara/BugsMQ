import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubService } from '../../services/saga-hub.service';
import { PagedResult, SagaSummary } from '../../models/saga.model';
import { SagaList } from './saga-list';

function makeSummary(overrides: Partial<SagaSummary> = {}): SagaSummary {
  return {
    correlationId: 'id-1',
    sagaType: 'OrderSaga',
    kind: 'Orchestrated',
    currentState: 'Completed',
    status: 'Completed',
    createdAtUtc: '2026-01-01T00:00:00Z',
    updatedAtUtc: '2026-01-01T00:00:00Z',
    version: 1,
    ...overrides,
  };
}

describe('SagaList', () => {
  let apiMock: { list: ReturnType<typeof vi.fn>; getSagaTypes: ReturnType<typeof vi.fn> };
  let hubMock: { sagaUpdated$: Subject<SagaSummary>; subscribeToList: ReturnType<typeof vi.fn> };

  function setup(listResult: PagedResult<SagaSummary> = { items: [], page: 1, pageSize: 50, totalCount: 0 }) {
    apiMock = {
      list: vi.fn().mockReturnValue(of(listResult)),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = {
      sagaUpdated$: new Subject<SagaSummary>(),
      subscribeToList: vi.fn().mockResolvedValue(undefined),
    };

    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });

    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();
    return fixture;
  }

  it('loads saga types and the saga list on init, and subscribes to live list updates', () => {
    const summary = makeSummary();
    const fixture = setup({ items: [summary], page: 1, pageSize: 50, totalCount: 1 });

    expect(apiMock.getSagaTypes).toHaveBeenCalled();
    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 50 }));
    expect(fixture.componentInstance.sagas()).toEqual([summary]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
    expect(hubMock.subscribeToList).toHaveBeenCalled();
  });

  it('shows an error banner when the list request fails', () => {
    apiMock = {
      list: vi.fn().mockReturnValue(throwError(() => new Error('network down'))),
      getSagaTypes: vi.fn().mockReturnValue(of([])),
    };
    hubMock = { sagaUpdated$: new Subject(), subscribeToList: vi.fn().mockResolvedValue(undefined) };

    TestBed.configureTestingModule({
      imports: [SagaList],
      providers: [
        provideRouter([]),
        { provide: SagaApiService, useValue: apiMock },
        { provide: SagaHubService, useValue: hubMock },
      ],
    });
    const fixture = TestBed.createComponent(SagaList);
    fixture.detectChanges();

    expect(fixture.componentInstance.error()).toContain('Could not reach');
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.nativeElement.querySelector('.banner--error')?.textContent).toContain('Could not reach');
  });

  it('refresh() re-queries the API with the current filter values', () => {
    const fixture = setup();
    fixture.componentInstance.status = 'Failed';
    fixture.componentInstance.sagaType = 'OrderSaga';

    fixture.componentInstance.refresh();

    expect(apiMock.list).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'Failed', sagaType: 'OrderSaga' }));
  });

  it('changing the status filter dropdown triggers a refresh with the new value', () => {
    const fixture = setup();
    apiMock.list.mockClear();

    const select: HTMLSelectElement = fixture.nativeElement.querySelectorAll('select')[0];
    select.value = 'Failed';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(apiMock.list).toHaveBeenCalledWith(expect.objectContaining({ status: 'Failed' }));
  });

  it('upserts an existing saga in place on a live update, leaving totalCount unchanged', () => {
    const original = makeSummary({ status: 'Running' });
    const fixture = setup({ items: [original], page: 1, pageSize: 50, totalCount: 1 });

    const updated = { ...original, status: 'Completed' as const };
    hubMock.sagaUpdated$.next(updated);

    expect(fixture.componentInstance.sagas()).toEqual([updated]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
  });

  it('prepends a new saga on a live update when it matches the active filter', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 50, totalCount: 0 });
    fixture.componentInstance.status = 'Failed';

    const incoming = makeSummary({ correlationId: 'new-1', status: 'Failed' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toEqual([incoming]);
    expect(fixture.componentInstance.totalCount()).toBe(1);
  });

  it('ignores a new saga on a live update when it does not match the active filter', () => {
    const fixture = setup({ items: [], page: 1, pageSize: 50, totalCount: 0 });
    fixture.componentInstance.status = 'Failed';

    const incoming = makeSummary({ correlationId: 'new-1', status: 'Running' });
    hubMock.sagaUpdated$.next(incoming);

    expect(fixture.componentInstance.sagas()).toEqual([]);
    expect(fixture.componentInstance.totalCount()).toBe(0);
  });
});
