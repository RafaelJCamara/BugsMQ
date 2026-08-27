import { TestBed } from '@angular/core/testing';
import { SagaMap as SagaMapModel } from '../../models/saga.model';
import { SagaMap } from './saga-map';

function makeMap(): SagaMapModel {
  return {
    summary: {
      correlationId: 'c1',
      sagaType: 'OrderSaga',
      kind: 'Orchestrated',
      currentState: 'Failed',
      status: 'Failed',
      createdAtUtc: '2026-01-01T00:00:00.000Z',
      updatedAtUtc: '2026-01-01T00:00:31.000Z',
      version: 1,
      parentSagaType: null,
      parentCorrelationId: null,
    },
    nodes: [
      { id: 'OrderSaga', displayName: 'OrderSaga', kind: 'Orchestrator', status: 'failed', messagesIn: 1, messagesOut: 2 },
      { id: 'OrderSubmitter', displayName: 'OrderSubmitter', kind: 'Initiator', status: 'ok', messagesIn: 0, messagesOut: 1 },
      { id: 'InventoryService', displayName: 'InventoryService', kind: 'Participant', status: 'ok', messagesIn: 1, messagesOut: 1 },
    ],
    edges: [
      {
        id: 'e1',
        fromNodeId: 'OrderSubmitter',
        toNodeId: 'OrderSaga',
        messageType: 'OrderSubmitted',
        messageId: 'm0',
        isCompensation: false,
        failed: false,
        unanswered: false,
        occurredAtUtc: '2026-01-01T00:00:00.000Z',
      },
      {
        id: 'e2',
        fromNodeId: 'OrderSaga',
        toNodeId: 'InventoryService',
        messageType: 'ReserveInventory',
        messageId: 'out-1',
        isCompensation: false,
        failed: false,
        unanswered: false,
        occurredAtUtc: '2026-01-01T00:00:00.300Z',
      },
    ],
    events: [
      { sequenceNumber: 1, edgeId: 'e1', nodeId: null, entryType: 'SagaStarted', messageType: 'OrderSubmitted', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.000Z' },
      { sequenceNumber: 2, edgeId: 'e2', nodeId: null, entryType: 'MessagePublished', messageType: 'ReserveInventory', errorMessage: null, occurredAtUtc: '2026-01-01T00:00:00.300Z' },
      { sequenceNumber: 3, edgeId: null, nodeId: 'OrderSaga', entryType: 'StepFailed', messageType: null, errorMessage: 'boom', occurredAtUtc: '2026-01-01T00:00:00.600Z' },
    ],
    failureEventIndex: 2,
  };
}

function createComponent(map: SagaMapModel = makeMap()) {
  const fixture = TestBed.createComponent(SagaMap);
  fixture.componentRef.setInput('map', map);
  fixture.detectChanges();
  return fixture;
}

describe('SagaMap', () => {
  it('starts at index 0 with all edges pending except the first', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;

    expect(component.currentIndex()).toBe(0);
    expect(component.edgeViews().find((e) => e.id === 'e1')?.state).toBe('active');
    expect(component.edgeViews().find((e) => e.id === 'e2')?.state).toBe('pending');
  });

  it('tick() advances progress and rolls over into the next step once the delay elapses', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.playing.set(true);

    component.tick(150); // e2's real gap clamps to the 1000ms minimum — this only advances progress
    expect(component.currentIndex()).toBe(0);
    expect(component.progress()).toBeGreaterThan(0);

    component.tick(1000); // enough to roll over
    expect(component.currentIndex()).toBe(1);
    expect(component.progress()).toBe(0);
  });

  it('force-pauses once it reaches the failure index', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.playing.set(true);
    component.currentIndex.set(1);

    component.tick(5000); // far more than any clamped delay — guarantees rollover to index 2 (the failure index)

    expect(component.currentIndex()).toBe(2);
    expect(component.playing()).toBe(false);
    expect(component.failureReached()).toBe(true);
  });

  it('scrubTo jumps directly to an index and stops playback', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.playing.set(true);

    component.scrubTo(2);

    expect(component.currentIndex()).toBe(2);
    expect(component.playing()).toBe(false);
    expect(component.failureReached()).toBe(true);
  });

  it('scrubTo clamps out-of-range indices', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;

    component.scrubTo(999);
    expect(component.currentIndex()).toBe(2);

    component.scrubTo(-5);
    expect(component.currentIndex()).toBe(0);
  });

  it('restart resets to the beginning', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.scrubTo(1);

    component.restart();

    expect(component.currentIndex()).toBe(0);
    expect(component.progress()).toBe(0);
  });

  it('applies the failed class to the orchestrator node once the failure step is reached', () => {
    const fixture = createComponent();
    const component = fixture.componentInstance;
    component.scrubTo(2);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    const failedNode = Array.from(el.querySelectorAll('.node--failed'));
    expect(failedNode.length).toBeGreaterThan(0);
  });

  it('renders the error card with the failure message once reached', () => {
    const fixture = createComponent();
    fixture.componentInstance.scrubTo(2);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.error-card')?.textContent).toContain('boom');
  });

  it('does not render the error card before the failure step', () => {
    const fixture = createComponent();
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.error-card')).toBeNull();
  });
});
