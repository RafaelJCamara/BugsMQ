import {
  CORRELATION_ID_HEADER,
  MESSAGE_ID_HEADER,
  MESSAGE_TYPE_HEADER,
  type MessageEnvelope,
  type MessageTransport,
  MessageTransportPublishError,
  type ReceivedMessage,
  type Subscription,
  TRACE_PARENT_HEADER,
  TRACE_STATE_HEADER,
  type TransportSubscription,
  VSAGA_HEADER_PREFIX,
  assertHeadersSafe,
  buildHeaders,
  isDashedGuid,
  normalizeHeaders,
} from '@vsaga/protocol';

import {
  HttpInboundDispatcher,
  NO_OP_ACK_CONTEXT,
  currentSyncReplyCollector,
} from './dispatcher.js';
import {
  type HttpTransportOptions,
  type ResolvedHttpTransportOptions,
  resolveOptions,
} from './options.js';
import { type HttpRouteTable, createConfigRouteTable } from './route-table.js';

/** One inbound HTTP request to `inboundPath`, in a shape any Node HTTP framework's own request object can be adapted to. */
export interface InboundHttpRequest {
  readonly headers: Readonly<Record<string, string | readonly string[] | undefined>>;
  readonly body: Buffer;
}

/** What a hosting adapter should write back as the HTTP response to an inbound request. */
export interface InboundHttpResponse {
  readonly status: 200 | 202 | 400;
  readonly headers?: Readonly<Record<string, string>>;
  readonly body?: Buffer;
}

/**
 * A vSaga-aware MessageTransport over plain HTTP, plus the framework-agnostic inbound entry point
 * every hosting adapter (`@vsaga/express`, `@vsaga/fastify`, `@vsaga/nestjs`, ...) wires its own
 * routing to -- the TypeScript analogue of `app.MapVSagaHttp()`
 * (dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs). vSaga ships no auth opinion
 * for this endpoint; adapters/callers apply their own.
 */
export interface HttpTransport extends MessageTransport {
  /** Path this service's own receive endpoint should be mapped to. */
  readonly inboundPath: string;
  /** Handles one inbound POST to `inboundPath`. Framework-agnostic: adapters translate their own request/response shape to and from this. */
  handleInboundRequest(request: InboundHttpRequest): Promise<InboundHttpResponse>;
}

/**
 * vSaga-aware, symmetric MessageTransport over plain HTTP: publish()/send() POST to
 * docs/design/http-based-sagas.md §4.2's wire format, and a 200 response with a full header set + body
 * is itself the reply, fed back into whichever local subscriber the reply's own message type
 * resolves to. No broker underneath -- see dispatcher.ts for how a reply is kept from re-entering
 * a saga while its own publishing step is still running, and how the ambient sync-reply collector
 * tells an unroutable publish from inside a handler apart from a routed one.
 *
 * Wire-compatible with dotnet/src/VSaga.Transport.Http/HttpMessageTransport.cs.
 */
export function createHttpTransport(options: HttpTransportOptions = {}): HttpTransport {
  const resolved = resolveOptions(options);
  return new HttpMessageTransportImpl(
    resolved,
    createConfigRouteTable(resolved),
    new HttpInboundDispatcher(),
  );
}

class HttpMessageTransportImpl implements HttpTransport {
  readonly #options: ResolvedHttpTransportOptions;
  readonly #routeTable: HttpRouteTable;
  readonly #dispatcher: HttpInboundDispatcher;

  constructor(
    options: ResolvedHttpTransportOptions,
    routeTable: HttpRouteTable,
    dispatcher: HttpInboundDispatcher,
  ) {
    this.#options = options;
    this.#routeTable = routeTable;
    this.#dispatcher = dispatcher;
  }

  get inboundPath(): string {
    return this.#options.inboundPath;
  }

  publish(
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal?: AbortSignal,
  ): Promise<void> {
    return this.#publishInternal(messageTypeName, body, envelope, undefined, signal);
  }

  send(
    destination: string,
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal?: AbortSignal,
  ): Promise<void> {
    return this.#publishInternal(messageTypeName, body, envelope, destination, signal);
  }

  subscribe(
    subscription: TransportSubscription,
    handler: (message: ReceivedMessage) => Promise<void>,
  ): Promise<Subscription> {
    return Promise.resolve(this.#dispatcher.subscribe(subscription, handler));
  }

  close(): Promise<void> {
    this.#dispatcher.close();
    return Promise.resolve();
  }

  /** Mirrors VSagaHttpEndpointExtensions.HandleInboundAsync. */
  async handleInboundRequest(request: InboundHttpRequest): Promise<InboundHttpResponse> {
    const headers = normalizeHeaders(request.headers);
    const messageTypeName = headers[MESSAGE_TYPE_HEADER];
    const messageId = headers[MESSAGE_ID_HEADER];
    const correlationId = headers[CORRELATION_ID_HEADER];

    if (!messageTypeName || !messageId || !correlationId || !isDashedGuid(correlationId)) {
      return { status: 400 };
    }

    const received: ReceivedMessage = {
      messageTypeName,
      correlationId,
      messageId,
      body: request.body,
      headers: extractVSagaHeaders(headers),
      ack: NO_OP_ACK_CONTEXT,
    };

    // CancellationToken.None on the .NET side, not the request's own -- deliberately not tying a
    // handler's own outbound calls (e.g. a fan-out reply back out over HTTP) to this inbound
    // connection's lifetime. There is no per-request signal threaded through dispatchInline here
    // for the same reason.
    const result = await this.#dispatcher.dispatchInline(received);

    if (!result.reply) return { status: 202 };

    return {
      status: 200,
      headers: {
        'content-type': 'application/json',
        ...buildHeaders(result.reply.envelope, result.reply.messageTypeName),
      },
      body: result.reply.body,
    };
  }

  /**
   * Resolves targets to the union of configured remote routes and local subscribers
   * (docs/design/http-based-sagas.md §3.3a) -- unroutable only when both are empty, in which case an
   * ambient sync-reply collector (present only while this call is running underneath a genuine
   * inbound HTTP request) gets first refusal at capturing it as that request's synchronous reply
   * (§3.2); only a message with a real destination, or one published outside any inline dispatch,
   * ever becomes a normal send/POST or a throw.
   */
  async #publishInternal(
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    explicitDestination: string | undefined,
    signal: AbortSignal | undefined,
  ): Promise<void> {
    assertHeadersSafe(envelope.headers);

    const remoteUrls =
      explicitDestination !== undefined
        ? this.#resolveExplicitDestination(explicitDestination)
        : this.#routeTable.resolveRemoteEndpoints(messageTypeName);

    // send()'s explicit destination bypasses routes entirely (§4.3) and therefore the local union
    // too -- a direct address is either configured or it isn't, mirroring RabbitMqTransport's
    // send() targeting a named queue with no exchange/binding lookup involved.
    const hasLocalSubscriber =
      explicitDestination === undefined && this.#dispatcher.hasLocalSubscriber(messageTypeName);

    if (remoteUrls.length === 0 && !hasLocalSubscriber) {
      const collector = currentSyncReplyCollector();
      if (collector?.tryCapture({ messageTypeName, body, envelope })) return;

      throw new MessageTransportPublishError(messageTypeName, envelope.correlationId, true);
    }

    if (hasLocalSubscriber) {
      this.#dispatcher.enqueueLocalDispatch({
        messageTypeName,
        correlationId: envelope.correlationId,
        messageId: envelope.messageId,
        body,
        headers: envelope.headers,
        ack: NO_OP_ACK_CONTEXT,
      });
    }

    if (remoteUrls.length === 1) {
      await this.#sendHttpRequest(remoteUrls[0]!, messageTypeName, body, envelope, signal);
    } else if (remoteUrls.length > 1) {
      await Promise.all(
        remoteUrls.map((url) =>
          this.#sendHttpRequest(url, messageTypeName, body, envelope, signal),
        ),
      );
    }
  }

  #resolveExplicitDestination(destination: string): readonly string[] {
    const url = this.#routeTable.resolveEndpointByName(destination);
    return url === undefined ? [] : [url];
  }

  async #sendHttpRequest(
    baseUrl: string,
    messageTypeName: string,
    body: Buffer,
    envelope: MessageEnvelope,
    signal: AbortSignal | undefined,
  ): Promise<void> {
    const timeoutSignal = AbortSignal.timeout(this.#options.requestTimeoutMs);
    const combinedSignal = signal ? AbortSignal.any([signal, timeoutSignal]) : timeoutSignal;

    let response: Response;
    try {
      response = await fetch(this.#buildRequestUrl(baseUrl), {
        method: 'POST',
        headers: { 'content-type': 'application/json', ...buildHeaders(envelope, messageTypeName) },
        body,
        signal: combinedSignal,
      });
    } catch (error) {
      throw new MessageTransportPublishError(messageTypeName, envelope.correlationId, false, {
        cause: error,
      });
    }

    if (response.status === 202) {
      await response.body?.cancel();
      return;
    }

    if (!response.ok) {
      await response.body?.cancel();
      throw new MessageTransportPublishError(messageTypeName, envelope.correlationId, false, {
        cause: new Error(
          `POST to ${baseUrl} for '${messageTypeName}' returned ${response.status} ${response.statusText}.`,
        ),
      });
    }

    await this.#handleSyncReply(response, messageTypeName, envelope.correlationId);
  }

  /**
   * A 200 IS the reply (docs/design/http-based-sagas.md §1, §4.2) -- fed back to whatever local
   * subscriber the reply's own type resolves to via enqueueLocalDispatch, never dispatched
   * inline: this call is itself running inside whatever gated dispatch published the original
   * message, so dispatching the reply inline would either deadlock on that same correlation's
   * gate or, worse, re-enter the saga before its own step has persisted (§3.1).
   */
  async #handleSyncReply(
    response: Response,
    originalMessageType: string,
    originalCorrelationId: string,
  ): Promise<void> {
    // Note: if an intermediary ever emits the same header name twice, the Fetch API's Headers
    // joins duplicates with ", " (comma-space), while .NET's ExtractVSagaHeaders joins with ","
    // (no space) -- a minor wire-format divergence for that narrow case. Not worth chasing: there
    // is no standard way to recover the original per-occurrence list from a Headers object to
    // rejoin it ourselves, and every header this transport itself ever sends is single-valued.
    const headers = normalizeHeaders(Object.fromEntries(response.headers.entries()));
    const replyTypeName = headers[MESSAGE_TYPE_HEADER];
    const replyMessageId = headers[MESSAGE_ID_HEADER];
    const replyCorrelationId = headers[CORRELATION_ID_HEADER];

    if (
      !replyTypeName ||
      !replyMessageId ||
      !replyCorrelationId ||
      !isDashedGuid(replyCorrelationId)
    ) {
      throw new MessageTransportPublishError(originalMessageType, originalCorrelationId, false, {
        cause: new Error(
          'HTTP 200 reply is missing one of the required x-vsaga- headers (message-type/correlation-id/message-id).',
        ),
      });
    }

    const replyBody = Buffer.from(await response.arrayBuffer());

    this.#dispatcher.enqueueLocalDispatch({
      messageTypeName: replyTypeName,
      correlationId: replyCorrelationId,
      messageId: replyMessageId,
      body: replyBody,
      headers: extractVSagaHeaders(headers),
      ack: NO_OP_ACK_CONTEXT,
    });
  }

  /**
   * Relative-merges `inboundPath` against `baseUrl` the same way `Uri`'s two-argument constructor
   * does on the .NET side (BuildRequestUri) rather than treating it as an absolute-path override,
   * so a `baseUrl` that already carries a sub-path merges identically on both runtimes.
   */
  #buildRequestUrl(baseUrl: string): string {
    const relativePath = this.#options.inboundPath.replace(/^\/+/, '');
    return new URL(relativePath, baseUrl).toString();
  }
}

/**
 * The three reserved headers plus every envelope header that arrived on the wire, filtered to the
 * `x-vsaga-` prefix -- mirrors HttpMessageTransport.ExtractVSagaHeaders /
 * VSagaHttpEndpointExtensions.ExtractVSagaHeaders. Node's http headers are already lower-cased by
 * the parser, which is what satisfies docs/design/http-based-sagas.md §3.3b's case-insensitivity
 * requirement without an explicit OrdinalIgnoreCase lookup -- the `.toLowerCase()` below is only
 * a defensive normalization for a hand-built (e.g. test) headers object.
 *
 * `traceparent`/`tracestate` are allowlisted by exact name alongside the prefix check -- the two
 * bare W3C trace context headers never carry the `x-vsaga-` prefix (interoperability is the whole
 * point), so they would otherwise be silently dropped here on both the inbound-request and
 * sync-reply paths.
 */
function extractVSagaHeaders(headers: Readonly<Record<string, string>>): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [key, value] of Object.entries(headers)) {
    const lowerKey = key.toLowerCase();
    if (
      lowerKey.startsWith(VSAGA_HEADER_PREFIX) ||
      lowerKey === TRACE_PARENT_HEADER ||
      lowerKey === TRACE_STATE_HEADER
    ) {
      result[lowerKey] = value;
    }
  }
  return result;
}
