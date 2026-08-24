import { Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubService } from '../../services/saga-hub.service';
import { SagaKind, SagaSortColumn, SagaStatus, SagaSummary, SagaTypeInfo } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';

const STATUSES: SagaStatus[] = ['Running', 'Completed', 'Failed', 'Compensating', 'Compensated', 'TimedOut', 'Cancelled'];
const KINDS: SagaKind[] = ['Orchestrated', 'Choreographed'];
const PAGE_SIZES = [25, 50, 75, 100];

type SortDirection = 'asc' | 'desc';

@Component({
  selector: 'app-saga-list',
  imports: [CommonModule, FormsModule, RouterLink, KindBadge, StatusBadge],
  templateUrl: './saga-list.html',
  styleUrl: './saga-list.scss',
})
export class SagaList implements OnInit, OnDestroy {
  readonly statuses = STATUSES;
  readonly kinds = KINDS;

  readonly sagas = signal<SagaSummary[]>([]);
  readonly sagaTypes = signal<SagaTypeInfo[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly totalCount = signal(0);

  readonly pageSizes = PAGE_SIZES;
  pageSize = PAGE_SIZES[0];
  readonly page = signal(1);
  readonly hasNextPage = computed(() => this.page() * this.pageSize < this.totalCount());

  /** Bumped instead of prepended when a live update matches the filter but we're off page 1 —
   * prepending there would silently show the wrong rows for the page the user is looking at. */
  readonly newSagasAvailable = signal(0);

  readonly sortColumn = signal<SagaSortColumn | null>(null);
  readonly sortDirection = signal<SortDirection>('asc');

  status: SagaStatus | '' = '';
  kind: SagaKind | '' = '';
  sagaType = '';
  search = '';

  private subs: Subscription[] = [];

  constructor(
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
  ) {}

  ngOnInit(): void {
    this.api.getSagaTypes().subscribe((types) => this.sagaTypes.set(types));
    this.refresh();

    void this.hub.subscribeToList();
    this.subs.push(
      this.hub.sagaUpdated$.subscribe((summary) => this.upsert(summary)),
    );
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
  }

  /** Filter changes must land back on page 1 — the previous page number may no longer exist under
   * the new filter, and showing a stale page's rows under a changed filter would be misleading. */
  onFilterChange(): void {
    this.page.set(1);
    this.refresh();
  }

  nextPage(): void {
    if (!this.hasNextPage()) return;
    this.page.set(this.page() + 1);
    this.refresh();
  }

  prevPage(): void {
    if (this.page() <= 1) return;
    this.page.set(this.page() - 1);
    this.refresh();
  }

  /** Sorting reorders the whole server-side result set, not just the rows already on screen — the
   * previous page number's rows would land somewhere else entirely under the new order, so (like a
   * filter change) this lands back on page 1 and re-fetches rather than reshuffling in place. */
  toggleSort(column: SagaSortColumn): void {
    if (this.sortColumn() === column) {
      this.sortDirection.set(this.sortDirection() === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
    this.page.set(1);
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);
    this.newSagasAvailable.set(0);

    this.api
      .list({
        status: this.status || undefined,
        kind: this.kind || undefined,
        sagaType: this.sagaType || undefined,
        search: this.search || undefined,
        page: this.page(),
        pageSize: this.pageSize,
        sortBy: this.sortColumn() ?? undefined,
        sortDescending: this.sortColumn() ? this.sortDirection() === 'desc' : undefined,
      })
      .subscribe({
        next: (result) => {
          this.sagas.set(result.items);
          this.totalCount.set(result.totalCount);
          this.loading.set(false);
        },
        error: () => {
          this.error.set('Could not reach the BugsMQ Dashboard API. Is it running?');
          this.loading.set(false);
        },
      });
  }

  private upsert(summary: SagaSummary): void {
    const current = this.sagas();
    const index = current.findIndex((s) => s.correlationId === summary.correlationId);

    if (index >= 0) {
      const next = current.slice();
      next[index] = summary;
      next.sort((a, b) => this.compareSagas(a, b));
      this.sagas.set(next);
      return;
    }

    if (!this.matchesFilter(summary)) return;

    this.totalCount.set(this.totalCount() + 1);

    if (this.page() === 1) {
      this.sagas.set([...current, summary].sort((a, b) => this.compareSagas(a, b)));
    } else {
      this.newSagasAvailable.set(this.newSagasAvailable() + 1);
    }
  }

  /** Keeps a live-patched page1 in the same order the server would return it in — an in-place status
   * update or a newly-inserted saga can change where a row belongs under the active sort, so a plain
   * prepend/replace would silently drift out of order until the next refresh(). */
  private compareSagas(a: SagaSummary, b: SagaSummary): number {
    const column = this.sortColumn();
    if (!column) return new Date(b.updatedAtUtc).getTime() - new Date(a.updatedAtUtc).getTime();

    const direction = this.sortDirection() === 'asc' ? 1 : -1;
    const valueA = column === 'Status' ? STATUSES.indexOf(a.status) : new Date(a.updatedAtUtc).getTime();
    const valueB = column === 'Status' ? STATUSES.indexOf(b.status) : new Date(b.updatedAtUtc).getTime();
    return (valueA - valueB) * direction;
  }

  private matchesFilter(summary: SagaSummary): boolean {
    if (this.status && summary.status !== this.status) return false;
    if (this.kind && summary.kind !== this.kind) return false;
    if (this.sagaType && summary.sagaType !== this.sagaType) return false;
    return true;
  }
}
