using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Dashboard.Api.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace BugsMQ.Dashboard.Api.Tests;

public sealed class DashboardTestState : SagaState;

public sealed class SagaEndpointsTests : IAsyncDisposable
{
    // Must match Program.cs's ConfigureHttpJsonOptions (enums as strings) so the client can parse
    // what the server actually sends back.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DashboardApiFactory _factory = new();
    private readonly HttpClient _client;

    public SagaEndpointsTests()
    {
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<Guid> SeedSagaAsync(string sagaType, string currentState, SagaStatus status, SagaKind kind = SagaKind.Orchestrated)
    {
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var store = _factory.Services.GetRequiredService<ISagaSnapshotStore<DashboardTestState>>();

        await store.InsertAsync(new DashboardTestState
        {
            CorrelationId = correlationId,
            SagaType = sagaType,
            Kind = kind,
            CurrentState = currentState,
            Status = status,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        return correlationId;
    }

    private Task AppendLogAsync(SagaLogEntry entry) =>
        _factory.Services.GetRequiredService<ISagaEventLogStore>().AppendAsync(entry);

    [Fact]
    public async Task ListSagas_WithNoData_ReturnsEmptyPage()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>("/api/sagas", JsonOptions);

        Assert.NotNull(result);
        Assert.Empty(result!.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ListSagas_FiltersByStatusKindAndSagaType()
    {
        var sagaType = $"OrderSaga-{Guid.NewGuid():N}";
        await SeedSagaAsync(sagaType, "Completed", SagaStatus.Completed);
        await SeedSagaAsync(sagaType, "Failed", SagaStatus.Failed);
        await SeedSagaAsync($"Other-{Guid.NewGuid():N}", "Failed", SagaStatus.Failed, SagaKind.Choreographed);

        var byType = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}", JsonOptions);
        Assert.Equal(2, byType!.TotalCount);

        var byStatus = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&status=Failed", JsonOptions);
        Assert.Equal(1, byStatus!.TotalCount);
        Assert.Equal(SagaStatus.Failed, byStatus.Items[0].Status);

        var byKind = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>("/api/sagas?kind=Choreographed", JsonOptions);
        Assert.Contains(byKind!.Items, s => s.Kind == SagaKind.Choreographed);
    }

    [Fact]
    public async Task ListSagas_RespectsPaging()
    {
        var sagaType = $"PagingSaga-{Guid.NewGuid():N}";
        for (var i = 0; i < 5; i++)
            await SeedSagaAsync(sagaType, "Running", SagaStatus.Running);

        var page1 = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&page=1&pageSize=2", JsonOptions);
        Assert.Equal(2, page1!.Items.Count);
        Assert.Equal(5, page1.TotalCount);

        var page3 = await _client.GetFromJsonAsync<PagedResult<SagaSummary>>($"/api/sagas?sagaType={sagaType}&page=3&pageSize=2", JsonOptions);
        Assert.Single(page3!.Items); // 5 items, page size 2 -> last page has 1
    }

    [Fact]
    public async Task GetSaga_Existing_ReturnsSummaryAndDataJson()
    {
        var correlationId = await SeedSagaAsync("OrderSaga", "Submitted", SagaStatus.Running);

        var detail = await _client.GetFromJsonAsync<SagaDetail>($"/api/sagas/{correlationId}", JsonOptions);

        Assert.NotNull(detail);
        Assert.Equal(correlationId, detail!.Summary.CorrelationId);
        Assert.Equal("Submitted", detail.Summary.CurrentState);
        Assert.NotNull(detail.DataJson);
    }

    [Fact]
    public async Task GetSaga_Missing_Returns404()
    {
        var response = await _client.GetAsync($"/api/sagas/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTimeline_ReturnsEntriesInOrder()
    {
        var correlationId = await SeedSagaAsync("OrderSaga", "AwaitingPayment", SagaStatus.Running);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted, toState: "Submitted"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepSucceeded, fromState: "Submitted", toState: "AwaitingInventory"));

        var timeline = await _client.GetFromJsonAsync<List<SagaLogEntry>>($"/api/sagas/{correlationId}/timeline", JsonOptions);

        Assert.NotNull(timeline);
        Assert.Equal(2, timeline!.Count);
        Assert.Equal(SagaEntryType.SagaStarted, timeline[0].EntryType);
        Assert.Equal(SagaEntryType.StepSucceeded, timeline[1].EntryType);
    }

    [Fact]
    public async Task GetSagaTypes_ReturnsDistinctSeenTypes()
    {
        var sagaType = $"TypeSaga-{Guid.NewGuid():N}";
        await SeedSagaAsync(sagaType, "A", SagaStatus.Running, SagaKind.Orchestrated);
        await SeedSagaAsync(sagaType, "B", SagaStatus.Completed, SagaKind.Orchestrated);

        var types = await _client.GetFromJsonAsync<List<SagaTypeInfo>>("/api/saga-types", JsonOptions);

        Assert.NotNull(types);
        Assert.Contains(types!, t => t.SagaType == sagaType && t.Kind == SagaKind.Orchestrated);
        // Only one entry per (SagaType, Kind) pair even though two instances share it.
        Assert.Single(types!, t => t.SagaType == sagaType);
    }

    [Fact]
    public async Task Retry_MissingSaga_Returns404()
    {
        var response = await _client.PostAsync($"/api/sagas/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Retry_SagaNotFailedOrTimedOut_Returns409()
    {
        var correlationId = await SeedSagaAsync("OrderSaga", "AwaitingPayment", SagaStatus.Running);

        var response = await _client.PostAsync($"/api/sagas/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Retry_WithNoTimelineHistory_Returns422()
    {
        var correlationId = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);

        var response = await _client.PostAsync($"/api/sagas/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Retry_TechnicalFailure_RedrivesTheExactFailedMessageWithAFreshId()
    {
        var correlationId = await SeedSagaAsync("OrderSaga", "AwaitingInventory", SagaStatus.Failed);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepFailed,
            fromState: "AwaitingInventory", messageType: "ReserveInventory", messageId: "m1",
            payloadJson: "{\"OrderId\":\"X\"}", errorMessage: "boom"));

        var response = await _client.PostAsync($"/api/sagas/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(_factory.Transport.Published, p =>
            p.MessageTypeName == "ReserveInventory" &&
            p.Envelope.CorrelationId == correlationId &&
            p.Envelope.MessageId != "m1"); // fresh id, not a re-delivery of the original

        var timeline = await _client.GetFromJsonAsync<List<SagaLogEntry>>($"/api/sagas/{correlationId}/timeline", JsonOptions);
        Assert.Contains(timeline!, e => e.EntryType == SagaEntryType.ManualRetryRequested);
    }

    [Fact]
    public async Task Retry_BusinessFailureWithNoStepFailed_ResetsToInitialStateAndReplaysTheStartingMessage()
    {
        // No StepFailed entry at all — this saga reached Failed via a normal, successful business
        // transition (e.g. "payment declined"), exactly the case that originally 422'd before the fix.
        var correlationId = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.Failed);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"));
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.StepSucceeded,
            fromState: "AwaitingInventory", toState: "Failed", messageType: "InventoryReservationFailed", messageId: "m1"));

        var response = await _client.PostAsync($"/api/sagas/{correlationId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var detail = await _client.GetFromJsonAsync<SagaDetail>($"/api/sagas/{correlationId}", JsonOptions);
        Assert.Equal("Submitted", detail!.Summary.CurrentState);
        Assert.Equal(SagaStatus.Running, detail.Summary.Status);

        Assert.Contains(_factory.Transport.Published, p =>
            p.MessageTypeName == "OrderSubmitted" &&
            p.Envelope.CorrelationId == correlationId &&
            p.Envelope.MessageId != "m0");
    }

    [Fact]
    public async Task Retry_TimedOutSaga_IsAllowed()
    {
        var correlationId = await SeedSagaAsync("OrderSaga", "Failed", SagaStatus.TimedOut);
        await AppendLogAsync(SagaLogEntry.Create(correlationId, "OrderSaga", SagaEntryType.SagaStarted,
            toState: "Submitted", messageType: "OrderSubmitted", messageId: "m0", payloadJson: "{\"OrderId\":\"X\"}"));

        var response = await _client.PostAsync($"/api/sagas/{correlationId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
