import { Injectable, OnDestroy } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { DASHBOARD_API_KEY, HUB_URL } from '../api-config';
import { SagaLogEntry, SagaSummary } from '../models/saga.model';

@Injectable({ providedIn: 'root' })
export class SagaHubService implements OnDestroy {
  private connection: signalR.HubConnection | null = null;
  private startPromise: Promise<void> | null = null;

  readonly sagaUpdated$ = new Subject<SagaSummary>();
  readonly timelineEntryAdded$ = new Subject<{ sagaType: string; correlationId: string; entry: SagaLogEntry }>();

  private async ensureStarted(): Promise<void> {
    if (!this.connection) {
      this.connection = new signalR.HubConnectionBuilder()
        .withUrl(HUB_URL, { accessTokenFactory: () => DASHBOARD_API_KEY })
        .withAutomaticReconnect()
        .build();

      this.connection.on('SagaUpdated', (summary: SagaSummary) => this.sagaUpdated$.next(summary));
      this.connection.on('TimelineEntryAdded', (sagaType: string, correlationId: string, entry: SagaLogEntry) =>
        this.timelineEntryAdded$.next({ sagaType, correlationId, entry }),
      );
    }

    if (!this.startPromise) {
      this.startPromise = this.connection.start().catch((err) => {
        this.startPromise = null;
        throw err;
      });
    }

    return this.startPromise;
  }

  async subscribeToList(): Promise<void> {
    await this.ensureStarted();
    await this.connection!.invoke('SubscribeToList');
  }

  // Hub groups are keyed by (sagaType, correlationId) server-side, so both are sent: two saga types
  // may track the same correlation id and a detail view must only receive its own instance's entries.
  async subscribeToSaga(sagaType: string, correlationId: string): Promise<void> {
    await this.ensureStarted();
    await this.connection!.invoke('SubscribeToSaga', sagaType, correlationId);
  }

  async unsubscribeFromSaga(sagaType: string, correlationId: string): Promise<void> {
    if (this.connection?.state === signalR.HubConnectionState.Connected) {
      await this.connection.invoke('UnsubscribeFromSaga', sagaType, correlationId);
    }
  }

  ngOnDestroy(): void {
    void this.connection?.stop();
  }
}
