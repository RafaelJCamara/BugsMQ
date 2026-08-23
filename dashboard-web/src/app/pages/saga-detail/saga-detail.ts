import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubService } from '../../services/saga-hub.service';
import { SagaDetail as SagaDetailModel, SagaLogEntry } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';

type Tab = 'timeline' | 'data';

@Component({
  selector: 'app-saga-detail',
  imports: [CommonModule, RouterLink, KindBadge, StatusBadge],
  templateUrl: './saga-detail.html',
  styleUrl: './saga-detail.scss',
})
export class SagaDetail implements OnInit, OnDestroy {
  correlationId = '';

  readonly detail = signal<SagaDetailModel | null>(null);
  readonly timeline = signal<SagaLogEntry[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly tab = signal<Tab>('timeline');
  readonly retrying = signal(false);
  readonly retryMessage = signal<string | null>(null);

  private subs: Subscription[] = [];

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
  ) {}

  ngOnInit(): void {
    this.correlationId = this.route.snapshot.paramMap.get('id') ?? '';
    this.load();

    void this.hub.subscribeToSaga(this.correlationId);
    this.subs.push(
      this.hub.sagaUpdated$.subscribe((summary) => {
        if (summary.correlationId === this.correlationId) {
          this.detail.update((current) => (current ? { ...current, summary } : current));
        }
      }),
      this.hub.timelineEntryAdded$.subscribe(({ correlationId, entry }) => {
        if (correlationId === this.correlationId) {
          this.timeline.update((entries) => [...entries, entry]);
        }
      }),
    );
  }

  ngOnDestroy(): void {
    void this.hub.unsubscribeFromSaga(this.correlationId);
    this.subs.forEach((s) => s.unsubscribe());
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get(this.correlationId).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not load this saga. It may not exist.');
        this.loading.set(false);
      },
    });

    this.api.getTimeline(this.correlationId).subscribe((entries) => this.timeline.set(entries));
  }

  setTab(tab: Tab): void {
    this.tab.set(tab);
  }

  get prettyDataJson(): string {
    const json = this.detail()?.dataJson;
    if (!json) return '';
    try {
      return JSON.stringify(JSON.parse(json), null, 2);
    } catch {
      return json;
    }
  }

  retry(): void {
    this.retrying.set(true);
    this.retryMessage.set(null);

    this.api.retry(this.correlationId).subscribe({
      next: () => {
        this.retrying.set(false);
        this.retryMessage.set('Retry accepted — redriving the failed step.');
      },
      error: (err) => {
        this.retrying.set(false);
        this.retryMessage.set(err?.error?.error ?? 'Retry failed.');
      },
    });
  }
}
