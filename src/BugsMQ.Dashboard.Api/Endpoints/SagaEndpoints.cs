using System.Text;
using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Abstractions.Transport;

namespace BugsMQ.Dashboard.Api.Endpoints;

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

        group.MapGet("/{correlationId:guid}", async (Guid correlationId, ISagaSummaryReader reader, CancellationToken ct) =>
        {
            var summary = await reader.GetAsync(correlationId, ct);
            if (summary is null)
                return Results.NotFound();

            var dataJson = await reader.GetDataJsonAsync(correlationId, ct);
            return Results.Ok(new SagaDetail(summary, dataJson));
        })
        .WithName("GetSaga");

        group.MapGet("/{correlationId:guid}/timeline", async (Guid correlationId, ISagaEventLogStore log, CancellationToken ct) =>
            Results.Ok(await log.GetTimelineAsync(correlationId, ct)))
        .WithName("GetSagaTimeline");

        group.MapGet("/{correlationId:guid}/map", GetSagaMapAsync)
        .WithName("GetSagaMap");

        group.MapPost("/{correlationId:guid}/retry", RetrySagaAsync)
        .WithName("RetrySaga");

        app.MapGet("/api/saga-types", async (ISagaSummaryReader reader, CancellationToken ct) => Results.Ok(await reader.GetSagaTypesAsync(ct)))
            .WithTags("Sagas")
            .WithName("ListSagaTypes")
            .RequireAuthorization();
    }

    private static async Task<IResult> GetSagaMapAsync(Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, IServiceTopologyStore topologyStore, CancellationToken ct)
    {
        var summary = await reader.GetAsync(correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        var timeline = await log.GetTimelineAsync(correlationId, ct);
        var topology = await topologyStore.GetAllAsync(ct);

        return Results.Ok(SagaMapBuilder.Build(summary, timeline, topology));
    }

    private static async Task<IResult> RetrySagaAsync(Guid correlationId, ISagaSummaryReader reader, ISagaEventLogStore log, ISagaAdminStore admin, IMessageTransport transport, CancellationToken ct)
    {
        var summary = await reader.GetAsync(correlationId, ct);
        if (summary is null)
            return Results.NotFound();

        if (summary.Status is not (SagaStatus.Failed or SagaStatus.TimedOut))
            return Results.Conflict(new { error = $"Saga '{correlationId}' cannot be retried while its status is '{summary.Status}'; only 'Failed' or 'TimedOut' sagas can be retried." });

        var timeline = await log.GetTimelineAsync(correlationId, ct);

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
                return Results.UnprocessableEntity(new { error = $"Saga '{correlationId}' has no recorded failure or start to retry from." });

            redrive = start;
            resetToState = start.ToState;
        }

        await log.AppendAsync(SagaLogEntry.Create(correlationId, summary.SagaType, SagaEntryType.ManualRetryRequested,
            fromState: summary.CurrentState, toState: resetToState, messageType: redrive.MessageType, messageId: redrive.MessageId), ct);

        if (!string.Equals(resetToState, summary.CurrentState, StringComparison.Ordinal))
            await admin.ResetStateAsync(correlationId, resetToState, SagaStatus.Running, ct);

        // Redrive by re-publishing the message with a fresh message id (so the dedupe check
        // doesn't discard it) and the same correlation id. This deliberately does not require the
        // dashboard to know the saga's TState/definition — whichever process actually runs that
        // saga's engine picks it up through its normal subscription, exactly like any other
        // delivery, and the orchestrator resumes Running on successful reprocessing.
        var body = Encoding.UTF8.GetBytes(redrive.PayloadJson!);

        try
        {
            await transport.PublishRawAsync(redrive.MessageType!, body, MessageEnvelope.New(correlationId), ct);
        }
        catch (MessageTransportPublishException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                detail: $"Saga '{correlationId}' could not be retried: {ex.Message}");
        }

        return Results.Accepted();
    }
}
