using System.Text;
using VSaga.Abstractions.Persistence;
using VSaga.Abstractions.Sagas;
using VSaga.Abstractions.Transport;

namespace VSaga.Dashboard.Api.Endpoints;

public sealed record SagaDetail(SagaSummary Summary, string? DataJson);

public static class SagaEndpoints
{
    public static void MapSagaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sagas").WithTags("Sagas").RequireAuthorization();

        group.MapGet("", async (ISagaSummaryReader reader, SagaStatus? status, string? sagaType, SagaKind? kind, string? search, int page = 1, int pageSize = 25, SagaSortColumn? sortBy = null, bool sortDescending = false, CancellationToken ct = default) =>
        {
            var filter = new SagaListFilter
            {
                Status = status,
                SagaType = sagaType,
                Kind = kind,
                Search = search,
                Page = page <= 0 ? 1 : page,
                PageSize = pageSize <= 0 ? 25 : pageSize,
                SortBy = sortBy,
                SortDescending = sortDescending,
            };

            return Results.Ok(await reader.ListAsync(filter, ct));
        })
        .WithName("ListSagas");

        // Every per-instance route is {sagaType}/{correlationId}: a correlation id alone no longer
        // identifies a saga instance, since two saga types may track the same one. Callers holding
        // only a correlation id resolve it first via /api/correlations/{correlationId} below.
        group.MapGet("/{sagaType}/{correlationId:guid}", async (string sagaType, Guid correlationId, ISagaSummaryReader reader, CancellationToken ct) =>
        {
            var summary = await reader.GetAsync(sagaType, correlationId, ct);
            if (summary is null)
                return Results.NotFound();

            var dataJson = await reader.GetDataJsonAsync(sagaType, correlationId, ct);
            return Results.Ok(new SagaDetail(summary, dataJson));
        })
        .WithName("GetSaga");

        group.MapGet("/{sagaType}/{correlationId:guid}/timeline", async (string sagaType, Guid correlationId, ISagaEventLogStore log, CancellationToken ct) =>
            Results.Ok(await log.GetTimelineAsync(sagaType, correlationId, ct)))
        .WithName("GetSagaTimeline");

        group.MapGet("/{sagaType}/{correlationId:guid}/map", GetSagaMapAsync)
        .WithName("GetSagaMap");

        // The sagas this one started via StartChildAsync. Deliberately not 404-ing on an unknown
        // parent: a saga with no children and a saga that does not exist both legitimately have an
        // empty child list, and the caller already has GET /{sagaType}/{correlationId} to tell them
        // apart. Children have their own correlation ids, so this is a different question from
        // /api/correlations/{id}, which finds saga types sharing one id.
        group.MapGet("/{sagaType}/{correlationId:guid}/children", async (string sagaType, Guid correlationId, ISagaSummaryReader reader, CancellationToken ct) =>
            Results.Ok(await reader.FindChildrenAsync(sagaType, correlationId, ct)))
        .WithName("GetSagaChildren");

        group.MapPost("/{sagaType}/{correlationId:guid}/retry", RetrySagaAsync)
        .WithName("RetrySaga");

        MapCrossInstanceEndpoints(app);
    }

    /// <summary>
    /// The two lookups that are not scoped to one saga instance, so they sit outside the
    /// <c>/api/sagas</c> group rather than under it. Split out of <see cref="MapSagaEndpoints"/> only
    /// for length.
    /// </summary>
    private static void MapCrossInstanceEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/saga-types", async (ISagaSummaryReader reader, CancellationToken ct) => Results.Ok(await reader.GetSagaTypesAsync(ct)))
            .WithTags("Sagas")
            .WithName("ListSagaTypes")
            .RequireAuthorization();

        // Deliberately a separate top-level path rather than /api/sagas/by-correlation/{id}, which
        // would sit in the same slot as {sagaType} and rely on literal-beats-parameter precedence to
        // disambiguate. Returns every saga instance tracking this correlation id — normally one, more
        // than one when several saga types observe the same business transaction. Note this is not the
        // sub-saga relation: a child has its own correlation id and is found via /children instead.
        app.MapGet("/api/correlations/{correlationId:guid}", async (Guid correlationId, ISagaSummaryReader reader, CancellationToken ct) =>
            Results.Ok(await reader.FindByCorrelationIdAsync(correlationId, ct)))
            .WithTags("Sagas")
            .WithName("FindSagasByCorrelationId")
            .RequireAuthorization();
    }

    private static async Task<IResult> GetSagaMapAsync(string sagaType, Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, IServiceTopologyStore topologyStore, CancellationToken ct)
    {
        var summary = await reader.GetAsync(sagaType, correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);
        var topology = await topologyStore.GetAllAsync(ct);

        return Results.Ok(SagaMapBuilder.Build(summary, timeline, topology));
    }

    private static async Task<IResult> RetrySagaAsync(string sagaType, Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, ISagaAdminStore admin, IMessageTransport transport, CancellationToken ct)
    {
        var summary = await reader.GetAsync(sagaType, correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        if (summary.Status is not (SagaStatus.Failed or SagaStatus.TimedOut))
            return Results.Conflict(new { error = $"Saga '{sagaType}' instance '{correlationId}' cannot be retried while its status is '{summary.Status}'; only 'Failed' or 'TimedOut' sagas can be retried." });

        var timeline = await log.GetTimelineAsync(sagaType, correlationId, ct);

        // Two distinct redrive shapes:
        //  1. A technical failure (an action threw) — StepFailed carries the exact message that
        //     failed; replay just that one message against the saga's current (unchanged) state.
        //  2. A business failure or timeout — the saga reached Failed/TimedOut through a normal,
        //     successful step (e.g. "payment declined"), so there is no StepFailed entry at all.
        //     Retry here means starting over: reset the saga back to its initial state and replay
        //     the message that originally started it (SagaStarted carries that payload).
        var lastFailure = timeline.LastOrDefault(e => e.EntryType == SagaEntryType.StepFailed);
        var redrive = lastFailure is { MessageType: not null, PayloadJson: not null } ? lastFailure : null;
        var resetToState = summary.CurrentState;

        if (redrive is null)
        {
            var start = timeline.FirstOrDefault(e => e.EntryType == SagaEntryType.SagaStarted);
            if (start is not { MessageType: not null, PayloadJson: not null, ToState: not null })
                return Results.UnprocessableEntity(new { error = $"Saga '{sagaType}' instance '{correlationId}' has no recorded failure or start to retry from." });

            redrive = start;
            resetToState = start.ToState;
        }

        await log.AppendAsync(SagaLogEntry.Create(correlationId, summary.SagaType, SagaEntryType.ManualRetryRequested,
            fromState: summary.CurrentState, toState: resetToState, messageType: redrive.MessageType, messageId: redrive.MessageId), ct);

        if (!string.Equals(resetToState, summary.CurrentState, StringComparison.Ordinal))
            await admin.ResetStateAsync(sagaType, correlationId, resetToState, SagaStatus.Running, ct);

        // Redrive by re-publishing the message with a fresh message id (so the dedupe check
        // doesn't discard it) and the same correlation id. This deliberately does not require the
        // dashboard to know the saga's TState/definition — whichever process actually runs that
        // saga's engine picks it up through its normal subscription, exactly like any other
        // delivery, and the orchestrator resumes Running on successful reprocessing.
        //
        // Note this republish is still correlation-id-addressed, so every saga type subscribed to
        // this message type sees it, not only `sagaType` — each one's own dedupe/initiation rules
        // then decide what to do with it. That is the same fan-out a first-time delivery has.
        var body = Encoding.UTF8.GetBytes(redrive.PayloadJson!);

        try
        {
            await transport.PublishRawAsync(redrive.MessageType!, body, MessageEnvelope.New(correlationId), ct);
        }
        catch (MessageTransportPublishException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: $"Saga '{sagaType}' instance '{correlationId}' could not be retried: {ex.Message}");
        }

        return Results.Accepted();
    }
}
