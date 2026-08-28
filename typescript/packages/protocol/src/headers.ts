/**
 * The vSaga header set. The `x-vsaga-` prefix is load-bearing, not decorative: both HTTP sides
 * filter inbound/outbound headers on it
 * (dotnet/src/VSaga.Transport.Http/HttpMessageTransport.cs ExtractVSagaHeaders,
 * dotnet/src/VSaga.Transport.Http/VSagaHttpEndpointExtensions.cs ExtractVSagaHeaders).
 *
 * The first three are stamped by the transport itself
 * (dotnet/src/VSaga.Transport.RabbitMQ/RabbitMqTransport.cs, `CorrelationIdHeader` and friends);
 * the rest ride on the envelope
 * (dotnet/src/VSaga.Abstractions/Transport/MessageEnvelope.cs).
 *
 * TRACE_PARENT_HEADER/TRACE_STATE_HEADER below are the one deliberate exception to the prefix
 * rule -- see their own doc comment.
 */
export const VSAGA_HEADER_PREFIX = 'x-vsaga-';

export const CORRELATION_ID_HEADER = 'x-vsaga-correlation-id';
export const MESSAGE_ID_HEADER = 'x-vsaga-message-id';
export const MESSAGE_TYPE_HEADER = 'x-vsaga-message-type';
export const SOURCE_SERVICE_HEADER = 'x-vsaga-source-service';
export const CAUSATION_ID_HEADER = 'x-vsaga-causation-id';
export const PARENT_SAGA_TYPE_HEADER = 'x-vsaga-parent-saga-type';
export const PARENT_CORRELATION_ID_HEADER = 'x-vsaga-parent-correlation-id';

/**
 * W3C Trace Context headers (production-readiness.md §6). Bare names, not `x-vsaga-`-prefixed --
 * interoperability with an OTel collector, a broker plugin, or a non-vSaga consumer is the entire
 * point of the W3C spec, so these are the one pair of vSaga-recognized headers deliberately left
 * out of `VSAGA_HEADER_PREFIX`'s scheme. `transport-http`'s own extractor allowlists them by exact
 * name instead, mirroring `HttpMessageTransport.ExtractVSagaHeaders` /
 * `VSagaHttpEndpointExtensions.ExtractVSagaHeaders` on the .NET side.
 */
export const TRACE_PARENT_HEADER = 'traceparent';
export const TRACE_STATE_HEADER = 'tracestate';

/**
 * Headers the engine owns and a participant must never echo back onto a reply. The .NET
 * participant base class builds its reply envelope from scratch rather than copying the inbound
 * headers (dotnet/samples/.../Participants/ParticipantService.cs ReplyAsync), and
 * `x-vsaga-delivery-attempt` in particular is the orchestrator's own redelivery budget
 * (dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs) -- echoing it would corrupt the count.
 *
 * `traceparent`/`tracestate` deliberately do NOT belong here even though the reply envelope is
 * also built from scratch: a reply belongs in the same trace as the message that caused it, so
 * these two must propagate forward, not be stripped like the headers above. `participant.ts`
 * threads them from `received.headers` into the reply's envelope explicitly instead.
 */
export const ENGINE_OWNED_HEADERS: readonly string[] = ['x-vsaga-delivery-attempt'];
