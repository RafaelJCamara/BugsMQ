import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE_URL } from '../api-config';
import {
  PagedResult,
  SagaDetail,
  SagaListFilter,
  SagaLogEntry,
  SagaMap,
  SagaSummary,
  SagaTypeInfo,
} from '../models/saga.model';

@Injectable({ providedIn: 'root' })
export class SagaApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${API_BASE_URL}/api/sagas`;

  list(filter: SagaListFilter): Observable<PagedResult<SagaSummary>> {
    let params = new HttpParams();
    if (filter.status) params = params.set('status', filter.status);
    if (filter.sagaType) params = params.set('sagaType', filter.sagaType);
    if (filter.kind) params = params.set('kind', filter.kind);
    if (filter.search) params = params.set('search', filter.search);
    params = params.set('page', filter.page ?? 1);
    params = params.set('pageSize', filter.pageSize ?? 25);
    if (filter.sortBy) params = params.set('sortBy', filter.sortBy).set('sortDescending', filter.sortDescending ?? false);

    return this.http.get<PagedResult<SagaSummary>>(this.baseUrl, { params });
  }

  get(correlationId: string): Observable<SagaDetail> {
    return this.http.get<SagaDetail>(`${this.baseUrl}/${correlationId}`);
  }

  getTimeline(correlationId: string): Observable<SagaLogEntry[]> {
    return this.http.get<SagaLogEntry[]>(`${this.baseUrl}/${correlationId}/timeline`);
  }

  getMap(correlationId: string): Observable<SagaMap> {
    return this.http.get<SagaMap>(`${this.baseUrl}/${correlationId}/map`);
  }

  retry(correlationId: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${correlationId}/retry`, {});
  }

  getSagaTypes(): Observable<SagaTypeInfo[]> {
    return this.http.get<SagaTypeInfo[]>(`${API_BASE_URL}/api/saga-types`);
  }
}
