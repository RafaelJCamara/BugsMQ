import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubService } from '../../services/saga-hub.service';
import { SagaKind, SagaStatus, SagaSummary, SagaTypeInfo } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';

const STATUSES: SagaStatus[] = ['Running', 'Completed', 'Failed', 'Compensating', 'Compensated', 'TimedOut', 'Cancelled'];
const KINDS: SagaKind[] = ['Orchestrated', 'Choreographed'];

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

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api
      .list({
        status: this.status || undefined,
        kind: this.kind || undefined,
        sagaType: this.sagaType || undefined,
        search: this.search || undefined,
        page: 1,
        pageSize: 50,
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
      this.sagas.set(next);
    } else if (this.matchesFilter(summary)) {
      this.sagas.set([summary, ...current]);
      this.totalCount.set(this.totalCount() + 1);
    }
  }

  private matchesFilter(summary: SagaSummary): boolean {
    if (this.status && summary.status !== this.status) return false;
    if (this.kind && summary.kind !== this.kind) return false;
    if (this.sagaType && summary.sagaType !== this.sagaType) return false;
    return true;
  }
}
