import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { SagaApiService } from '../../services/saga-api.service';
import { SagaHubService } from '../../services/saga-hub.service';
import { SagaDetail as SagaDetailModel, SagaLogEntry, SagaMap as SagaMapModel, SagaSummary } from '../../models/saga.model';
import { KindBadge } from '../../components/kind-badge/kind-badge';
import { StatusBadge } from '../../components/status-badge/status-badge';
import { SagaMap } from '../../components/saga-map/saga-map';

type Tab = 'timeline' | 'data' | 'map';

@Component({
  selector: 'app-saga-detail',
  imports: [CommonModule, RouterLink, KindBadge, StatusBadge, SagaMap],
  templateUrl: './saga-detail.html',
  styleUrl: './saga-detail.scss',
})
export class SagaDetail implements OnInit, OnDestroy {
  correlationId = '';
  sagaType = '';

  readonly detail = signal<SagaDetailModel | null>(null);
  readonly timeline = signal<SagaLogEntry[]>([]);
  readonly map = signal<SagaMapModel | null>(null);
  /** Other saga types tracking this same correlation id — empty for the usual one-saga case. */
  readonly related = signal<SagaSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly tab = signal<Tab>('map');
  readonly retrying = signal(false);
  readonly retryMessage = signal<string | null>(null);

  private subs: Subscription[] = [];

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: SagaApiService,
    private readonly hub: SagaHubService,
  ) {}

  ngOnInit(): void {
    this.sagaType = this.route.snapshot.paramMap.get('sagaType') ?? '';
    this.correlationId = this.route.snapshot.paramMap.get('id') ?? '';
    this.load();

    void this.hub.subscribeToSaga(this.sagaType, this.correlationId);
    this.subs.push(
      this.hub.sagaUpdated$.subscribe((summary) => {
        // Both halves must match: the list group pushes updates for every saga, and another saga
        // type may be tracking this same correlation id.
        if (summary.correlationId === this.correlationId && summary.sagaType === this.sagaType) {
          this.detail.update((current) => (current ? { ...current, summary } : current));
          // The map isn't pushed incrementally like the timeline (SagaChangePollingService only ever
          // emits SagaUpdated, never TimelineEntryAdded, across processes) — re-fetch it whole instead.
          this.loadMap();
          this.loadRelated();
        }
      }),
      this.hub.timelineEntryAdded$.subscribe(({ sagaType, correlationId, entry }) => {
        if (correlationId === this.correlationId && sagaType === this.sagaType) {
          this.timeline.update((entries) => [...entries, entry]);
        }
      }),
    );
  }

  ngOnDestroy(): void {
    void this.hub.unsubscribeFromSaga(this.sagaType, this.correlationId);
    this.subs.forEach((s) => s.unsubscribe());
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get(this.sagaType, this.correlationId).subscribe({
      next: (detail) => {
        this.detail.set(detail);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not load this saga. It may not exist.');
        this.loading.set(false);
      },
    });

    this.api.getTimeline(this.sagaType, this.correlationId).subscribe((entries) => this.timeline.set(entries));
    this.loadMap();
    this.loadRelated();
  }

  /**
   * A correlation id can be tracked by more than one saga type — in the OrderProcessing sample,
   * OrderSaga and PostShipmentChoreography share one per order. This resolves the id to every
   * instance under it and drops this page's own, leaving just the siblings to link to.
   *
   * Snapshot rather than live: this page joins only its own instance's hub group, so a sibling's
   * status changes aren't pushed here. Refreshed whenever this saga itself updates, the same
   * compromise the map tab already makes. A failure is swallowed — a missing cross-link must not
   * take down a detail page that is otherwise fine.
   */
  loadRelated(): void {
    this.api.findByCorrelationId(this.correlationId).subscribe({
      next: (all) => this.related.set(all.filter((s) => s.sagaType !== this.sagaType)),
      error: () => this.related.set([]),
    });
  }

  loadMap(): void {
    this.api.getMap(this.sagaType, this.correlationId).subscribe((map) => this.map.set(map));
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

    this.api.retry(this.sagaType, this.correlationId).subscribe({
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
