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
 * Headers the engine owns and a participant must never echo back onto a reply. The .NET
 * participant base class builds its reply envelope from scratch rather than copying the inbound
 * headers (dotnet/samples/.../Participants/ParticipantService.cs ReplyAsync), and
 * `x-vsaga-delivery-attempt` in particular is the orchestrator's own redelivery budget
 * (dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs) -- echoing it would corrupt the count.
 */
export const ENGINE_OWNED_HEADERS: readonly string[] = ['x-vsaga-delivery-attempt'];
