import { afterEach, describe, expect, it } from 'vitest';
import {
  CAUSATION_ID_HEADER,
  CORRELATION_ID_HEADER,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  PARENT_CORRELATION_ID_HEADER,
  PARENT_SAGA_TYPE_HEADER,
  type ReceivedMessage,
  SOURCE_SERVICE_HEADER,
  TRACE_PARENT_HEADER,
  TRACE_STATE_HEADER,
  envelopeFrom,
  newCorrelationId,
  newEnvelope,
  newMessageId,
} from '@vsaga/protocol';

import { type TestNode, startTestNode } from './test-node.js';

/** TaskCompletionSource-alike: lets a test observe both "has it settled yet" and await the eventual value. */
interface Deferred<T> {
  readonly promise: Promise<T>;
  settled: boolean;
  resolve(value: T): void;
}

function deferred<T>(): Deferred<T> {
  let resolveFn!: (value: T) => void;
  const promise = new Promise<T>((resolve) => {
    resolveFn = resolve;
  });
  const self: Deferred<T> = {
    promise,
    settled: false,
    resolve(value) {
      self.settled = true;
      resolveFn(value);
    },
  };
  return self;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Mirrors dotnet/tests/VSaga.Transport.Http.Tests/HttpTransportTests.cs: the same nine scenarios,
 * each hosted over real localhost sockets (test-node.ts) rather than an in-memory TestServer,
 * since two vSaga services talking without a broker is this adapter's whole point.
 */
describe('createHttpTransport', () => {
  const nodes: TestNode[] = [];

  afterEach(async () => {
    await Promise.all(nodes.splice(0).map((n) => n.close()));
  });

  async function node(): Promise<TestNode> {
    const testNode = await startTestNode();
    nodes.push(testNode);
    return testNode;
  }

  it('delivers a published message to a remote subscriber with correlation id and type intact', async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind();
    const sender = senderNode.bind({
      endpoints: { receiver: receiverNode.baseUrl },
      routes: { PingMessage: ['receiver'] },
    });

    const received = deferred<ReceivedMessage>();
    await receiver.subscribe(
      {
        consumerName: 'TestConsumer',
        messageTypeNames: ['PingMessage'],
        queueNameHint: 'receiver-ping-queue',
      },
      async (message) => received.resolve(message),
    );

    const correlationId = newCorrelationId();
    await sender.publish(
      'PingMessage',
      Buffer.from(JSON.stringify({ text: 'hello' })),
      newEnvelope(correlationId),
    );

    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
    expect(message.messageTypeName).toBe('PingMessage');
    expect(JSON.parse(message.body.toString('utf8'))).toEqual({ text: 'hello' });
  });

  it('send() delivers directly to a named endpoint, bypassing routes entirely', async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind();
    const sender = senderNode.bind({ endpoints: { receiver: receiverNode.baseUrl } });

    const received = deferred<ReceivedMessage>();
    await receiver.subscribe(
      {
        consumerName: 'TestConsumer2',
        messageTypeNames: ['PingMessage'],
        queueNameHint: 'receiver-direct-queue',
      },
      async (message) => received.resolve(message),
    );

    // No routes entry for PingMessage at all -- send() resolves "receiver" as an endpoint name
    // directly (docs/http-based-sagas.md §4.3's AMQP-default-exchange analogue).
    const correlationId = newCorrelationId();
    await sender.send(
      'receiver',
      'PingMessage',
      Buffer.from(JSON.stringify({ text: 'direct' })),
      newEnvelope(correlationId),
    );

    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
  });

  it('publish() to an unrouted message type throws an unroutable publish error', async () => {
    const lonely = (await node()).bind();

    await expect(
      lonely.publish('PingMessage', Buffer.from('{}'), newEnvelope(newCorrelationId())),
    ).rejects.toMatchObject({ name: 'MessageTransportPublishError', isUnroutable: true });
  });

  it('propagates every x-vsaga- envelope header unchanged', async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind();
    const sender = senderNode.bind({
      endpoints: { receiver: receiverNode.baseUrl },
      routes: { PingMessage: ['receiver'] },
    });

    const received = deferred<ReceivedMessage>();
    await receiver.subscribe(
      {
        consumerName: 'TestConsumer3',
        messageTypeNames: ['PingMessage'],
        queueNameHint: 'receiver-headers-queue',
      },
      async (message) => received.resolve(message),
    );

    const correlationId = newCorrelationId();
    const headers = {
      [SOURCE_SERVICE_HEADER]: 'orders-service',
      [CAUSATION_ID_HEADER]: `causation-${newMessageId()}`,
      [PARENT_SAGA_TYPE_HEADER]: 'PostShipmentChoreography',
      [PARENT_CORRELATION_ID_HEADER]: newCorrelationId(),
    };

    await sender.publish('PingMessage', Buffer.from('{}'), {
      correlationId,
      messageId: newMessageId(),
      headers,
    });

    const message = await received.promise;
    expect(message.headers[SOURCE_SERVICE_HEADER]).toBe('orders-service');
    expect(message.headers[CAUSATION_ID_HEADER]).toBe(headers[CAUSATION_ID_HEADER]);
    expect(message.headers[PARENT_SAGA_TYPE_HEADER]).toBe('PostShipmentChoreography');
    expect(message.headers[PARENT_CORRELATION_ID_HEADER]).toBe(
      headers[PARENT_CORRELATION_ID_HEADER],
    );
  });

  /**
   * docs/http-based-sagas.md §4.2: x-vsaga-message-type has no home in the envelope, and the
   * response path is exactly where it's easy to forget to stamp it. If missing, the sender can't
   * identify the reply and throws from inside the awaited publish() below.
   */
  it('a synchronous reply carries the full envelope, including message-type, on the response path', async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind();
    const sender = senderNode.bind({
      endpoints: { receiver: receiverNode.baseUrl },
      routes: { Command: ['receiver'] },
    });

    // Reply has no route/local subscriber on the receiver side -- unroutable, so it's captured as
    // this handler's own synchronous reply to the inbound Command it's currently handling.
    await receiver.subscribe(
      {
        consumerName: 'Receiver',
        messageTypeNames: ['Command'],
        queueNameHint: 'receiver-command-queue',
      },
      async (message) => {
        await receiver.publish(
          'Reply',
          Buffer.from(JSON.stringify({ text: 'ok' })),
          envelopeFrom('Receiver', message.correlationId, message.messageId),
        );
      },
    );

    const replyReceived = deferred<ReceivedMessage>();
    await sender.subscribe(
      {
        consumerName: 'ReplyListener',
        messageTypeNames: ['Reply'],
        queueNameHint: 'sender-reply-queue',
      },
      async (message) => replyReceived.resolve(message),
    );

    const correlationId = newCorrelationId();
    await sender.publish(
      'Command',
      Buffer.from(JSON.stringify({ text: 'charge' })),
      newEnvelope(correlationId),
    );

    const reply = await replyReceived.promise;
    expect(reply.messageTypeName).toBe('Reply');
    expect(reply.correlationId).toBe(correlationId);
    expect(JSON.parse(reply.body.toString('utf8'))).toEqual({ text: 'ok' });
  });

  /**
   * docs/http-based-sagas.md §3.1: the reply must not re-enter while its own publishing step is
   * still running. Proven deterministically, not by timing -- the reply's dispatch needs the same
   * correlation id's gate that the trigger's still-running dispatch is holding, so it cannot have
   * run by the time the trigger handler (which awaited the full HTTP round trip) makes its
   * assertion, no matter how fast that round trip was. The 300ms delay only rules out the reply
   * arriving *later*, off of scheduling luck, once the gate is (wrongly) not blocking it.
   */
  it('does not dispatch a synchronous reply until the publishing step releases its correlation gate', async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind();
    const sender = senderNode.bind({
      endpoints: { receiver: receiverNode.baseUrl },
      routes: { Command: ['receiver'] },
    });

    await receiver.subscribe(
      {
        consumerName: 'Receiver',
        messageTypeNames: ['Command'],
        queueNameHint: 'receiver-command-queue',
      },
      async (message) => {
        await receiver.publish(
          'Reply',
          Buffer.from(JSON.stringify({ text: 'ok' })),
          envelopeFrom('Receiver', message.correlationId, message.messageId),
        );
      },
    );

    const replySeen = deferred<boolean>();
    await sender.subscribe(
      {
        consumerName: 'ReplyListener',
        messageTypeNames: ['Reply'],
        queueNameHint: 'sender-reply-queue',
      },
      async () => replySeen.resolve(true),
    );

    const releaseGate = deferred<void>();
    const commandRoundTripDone = deferred<void>();

    await sender.subscribe(
      {
        consumerName: 'TriggerListener',
        messageTypeNames: ['Trigger'],
        queueNameHint: 'sender-trigger-queue',
      },
      async (message) => {
        await sender.publish('Command', Buffer.from('{}'), newEnvelope(message.correlationId));
        commandRoundTripDone.resolve();
        await releaseGate.promise; // keep holding this correlation's gate open
      },
    );

    const correlationId = newCorrelationId();
    await sender.publish('Trigger', Buffer.from('{}'), newEnvelope(correlationId));

    await commandRoundTripDone.promise;
    await delay(300);
    expect(replySeen.settled).toBe(false);

    releaseGate.resolve();

    // ...but it does arrive once the trigger's own dispatch finishes and releases the gate.
    expect(await replySeen.promise).toBe(true);
  });

  /** docs/http-based-sagas.md §3.3a: local subscriptions are part of the route table, exactly like the orchestrator's own-type redelivery relies on over RabbitMQ's routing table. */
  it('re-enters a local subscriber for its own type even with no remote route configured (redelivery)', async () => {
    const solo = (await node()).bind();

    const received = deferred<ReceivedMessage>();
    await solo.subscribe(
      {
        consumerName: 'SelfConsumer',
        messageTypeNames: ['RedeliverableCommand'],
        queueNameHint: 'solo-redeliver-queue',
      },
      async (message) => received.resolve(message),
    );

    // No endpoints/routes configured for this type at all -- routable only because of the local
    // subscriber above.
    const correlationId = newCorrelationId();
    await solo.publish(
      'RedeliverableCommand',
      Buffer.from(JSON.stringify({ text: 'retry-me' })),
      newEnvelope(correlationId),
    );

    const message = await received.promise;
    expect(message.correlationId).toBe(correlationId);
    expect(message.messageTypeName).toBe('RedeliverableCommand');
  });

  /** docs/http-based-sagas.md §3.3b: header names are case-insensitive on the wire; Node's http parser normalizes them to lowercase on receipt, which is what satisfies this without any explicit casing logic. */
  it('a mixed-case delivery-attempt header survives the round trip', async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind();
    const sender = senderNode.bind({
      endpoints: { receiver: receiverNode.baseUrl },
      routes: { PingMessage: ['receiver'] },
    });

    const received = deferred<ReceivedMessage>();
    await receiver.subscribe(
      {
        consumerName: 'TestConsumer',
        messageTypeNames: ['PingMessage'],
        queueNameHint: 'receiver-attempt-queue',
      },
      async (message) => received.resolve(message),
    );

    const correlationId = newCorrelationId();
    await sender.publish('PingMessage', Buffer.from(JSON.stringify({ text: 'redelivered' })), {
      correlationId,
      messageId: newMessageId(),
      // Deliberately non-canonical casing, simulating a proxy (or another adapter's client) that
      // normalizes header names differently.
      headers: { 'X-VSaga-Delivery-Attempt': '3' },
    });

    const message = await received.promise;
    expect(message.headers['x-vsaga-delivery-attempt']).toBe('3');
  });

  /**
   * production-readiness.md §6/§8.17: `traceparent`/`tracestate` carry no `x-vsaga-` prefix on
   * purpose (interoperability with a non-vSaga consumer is the point), so extractVSagaHeaders needs
   * the two bare names allowlisted explicitly or they're silently dropped by the prefix filter.
   * Exercises both directions of one HTTP round trip: the inbound publish and the synchronous reply
   * that comes back, mirroring dotnet/tests/VSaga.Transport.Http.Tests's own version of this test.
   */
  it('traceparent and tracestate headers survive both directions of an HTTP round trip', async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind();
    const sender = senderNode.bind({
      endpoints: { receiver: receiverNode.baseUrl },
      routes: { Command: ['receiver'] },
    });

    const traceParent = '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01';
    const traceState = 'vendor1=value1,vendor2=value2';

    // Reply carries the same trace context back, exactly as an instrumented participant threading
    // the inbound trace onto its reply would -- unroutable on the receiver side, so it's captured as
    // this handler's own synchronous reply.
    await receiver.subscribe(
      {
        consumerName: 'TraceReceiver',
        messageTypeNames: ['Command'],
        queueNameHint: 'receiver-trace-queue',
      },
      async (message) => {
        expect(message.headers[TRACE_PARENT_HEADER]).toBe(traceParent);
        expect(message.headers[TRACE_STATE_HEADER]).toBe(traceState);

        await receiver.publish('Reply', Buffer.from(JSON.stringify({ text: 'ok' })), {
          correlationId: message.correlationId,
          messageId: newMessageId(),
          headers: { [TRACE_PARENT_HEADER]: traceParent, [TRACE_STATE_HEADER]: traceState },
        });
      },
    );

    const replyReceived = deferred<ReceivedMessage>();
    await sender.subscribe(
      {
        consumerName: 'TraceReplyListener',
        messageTypeNames: ['Reply'],
        queueNameHint: 'sender-trace-reply-queue',
      },
      async (message) => replyReceived.resolve(message),
    );

    const correlationId = newCorrelationId();
    await sender.publish('Command', Buffer.from(JSON.stringify({ text: 'charge' })), {
      correlationId,
      messageId: newMessageId(),
      headers: { [TRACE_PARENT_HEADER]: traceParent, [TRACE_STATE_HEADER]: traceState },
    });

    const reply = await replyReceived.promise;
    expect(reply.headers[TRACE_PARENT_HEADER]).toBe(traceParent);
    expect(reply.headers[TRACE_STATE_HEADER]).toBe(traceState);
  });

  /**
   * docs/http-based-sagas.md §3.2: a message with a real route (the ShipOrder case) must go out
   * as a normal POST, never be swallowed as the currently-in-flight inbound request's own
   * synchronous reply, even though both are published from inside the very same inline dispatch.
   */
  it("a routed publish from inside a handler is not captured as that handler's own sync reply", async () => {
    const receiverNode = await node();
    const senderNode = await node();

    const receiver = receiverNode.bind({
      endpoints: { sender: senderNode.baseUrl },
      routes: { RoutedSideEffect: ['sender'] },
    });
    const sender = senderNode.bind({
      endpoints: { receiver: receiverNode.baseUrl },
      routes: { Trigger: ['receiver'] },
    });

    await receiver.subscribe(
      {
        consumerName: 'Receiver',
        messageTypeNames: ['Trigger'],
        queueNameHint: 'receiver-trigger-queue',
      },
      async (message) => {
        await receiver.publish(
          'RoutedSideEffect',
          Buffer.from('{}'),
          newEnvelope(message.correlationId),
        );
      },
    );

    const sideEffectReceived = deferred<ReceivedMessage>();
    await sender.subscribe(
      {
        consumerName: 'SideEffectListener',
        messageTypeNames: ['RoutedSideEffect'],
        queueNameHint: 'sender-sideeffect-queue',
      },
      async (message) => sideEffectReceived.resolve(message),
    );

    // Completes with an ordinary 202 from the receiver -- nothing was captured as Trigger's
    // reply, because RoutedSideEffect resolved to a real destination instead. That also makes
    // this a genuine, independent, inline-awaited inbound request on the sender's own endpoint:
    // it must have already reached SideEffectListener by the time this call returns, since
    // receiver's own response to Trigger isn't written until *its* handler (which awaits
    // publishing RoutedSideEffect to completion) finishes.
    const correlationId = newCorrelationId();
    await sender.publish('Trigger', Buffer.from('{}'), newEnvelope(correlationId));
    expect(sideEffectReceived.settled).toBe(true);

    const message = await sideEffectReceived.promise;
    expect(message.correlationId).toBe(correlationId);
    expect(message.messageTypeName).toBe('RoutedSideEffect');
  });

  /**
   * Regression test for a bug caught by review: enqueueLocalDispatch's fire-and-forget task used
   * to inherit whatever ambient sync-reply collector was active at its call site. A background
   * dispatch spawned from inside an active inline dispatch (an ordinary same-process publish from
   * a handler, followed by further awaited work) would then be able to hijack an unrelated,
   * concurrently in-flight inbound request's reply slot.
   */
  it("a background dispatch spawned from inside an active inbound dispatch does not inherit that request's sync-reply collector", async () => {
    const testNode = await node();
    const transport = testNode.bind();

    await transport.subscribe(
      {
        consumerName: 'TriggerHandler',
        messageTypeNames: ['Trigger'],
        queueNameHint: 'trigger-queue',
      },
      async (message) => {
        // Ordinary usage, not an anti-pattern: a same-process publish to a locally-subscribed
        // type, then more awaited work before this handler (and hence its own inline dispatch)
        // finishes.
        await transport.publish(
          'ChildStart',
          Buffer.from('{}'),
          newEnvelope(message.correlationId),
        );
        await delay(50);
      },
    );

    const childOutcome = deferred<boolean>();
    await transport.subscribe(
      {
        consumerName: 'ChildStartHandler',
        messageTypeNames: ['ChildStart'],
        queueNameHint: 'child-queue',
      },
      async (message) => {
        // Unroutable: no route, no local subscriber. If this wrongly captured Trigger's
        // still-open collector, it would surface as Trigger's inbound HTTP response instead of
        // throwing here.
        try {
          await transport.publish(
            'ChildUnroutable',
            Buffer.from('{}'),
            newEnvelope(message.correlationId),
          );
          childOutcome.resolve(false);
        } catch {
          childOutcome.resolve(true);
        }
      },
    );

    const correlationId = newCorrelationId();
    const envelope = newEnvelope(correlationId);
    const response = await fetch(`${testNode.baseUrl}${transport.inboundPath}`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        [MESSAGE_TYPE_HEADER]: 'Trigger',
        [MESSAGE_ID_HEADER]: envelope.messageId,
        [CORRELATION_ID_HEADER]: correlationId,
      },
      body: Buffer.from('{}'),
    });
    await response.arrayBuffer();

    // Trigger's own handler never published anything unroutable -- the unrelated ChildStart
    // dispatch must not have hijacked this request's reply slot.
    expect(response.status).toBe(202);
    expect(await childOutcome.promise).toBe(true);
  });

  /**
   * Regression test for a bug caught by review: close() used to clear the subscriber Map
   * unconditionally, which silently truncated a multi-subscriber fan-out already paused
   * mid-iteration (Map iteration ends early once the Map being iterated is cleared).
   */
  it('close() does not truncate a multi-subscriber fan-out already in progress', async () => {
    const transport = (await node()).bind();

    const firstHandlerStarted = deferred<void>();
    const releaseFirstHandler = deferred<void>();
    const secondHandlerRan = deferred<boolean>();

    await transport.subscribe(
      { consumerName: 'First', messageTypeNames: ['FanOut'], queueNameHint: 'fanout-first' },
      async () => {
        firstHandlerStarted.resolve();
        await releaseFirstHandler.promise;
      },
    );
    await transport.subscribe(
      { consumerName: 'Second', messageTypeNames: ['FanOut'], queueNameHint: 'fanout-second' },
      async () => {
        secondHandlerRan.resolve(true);
      },
    );

    await transport.publish('FanOut', Buffer.from('{}'), newEnvelope(newCorrelationId()));

    await firstHandlerStarted.promise;
    await transport.close(); // must not truncate the fan-out already in progress for this message
    releaseFirstHandler.resolve();

    expect(await secondHandlerRan.promise).toBe(true);
  });
});
