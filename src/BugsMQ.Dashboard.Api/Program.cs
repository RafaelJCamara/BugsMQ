using BugsMQ.Dashboard.Api;
using BugsMQ.Dashboard.Api.Endpoints;
using BugsMQ.Dashboard.Api.Hubs;
using BugsMQ.Abstractions.Notifications;
using BugsMQ.Observability;
using BugsMQ.Persistence.EFCore;
using BugsMQ.Transport.RabbitMQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Serialize enums (SagaKind/SagaStatus/SagaEntryType) as their string names, not raw ints — both over
// plain HTTP JSON and over the SignalR hub's payloads, so the dashboard doesn't need its own
// int<->name mapping table for every enum.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

const string CorsPolicy = "Dashboard";
var allowedOrigin = builder.Configuration["Dashboard:WebOrigin"] ?? "http://localhost:4200";
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// Dashboard.Api deliberately never calls AddBugsMqEngine/AddSaga<>() — it stays generic across any
// number of saga types by reading persisted data (ISagaSummaryReader/ISagaEventLogStore) rather than
// requiring its own copy of every saga definition. Retry works the same way: it redrives via a raw
// transport republish (see SagaEndpoints), not an in-process orchestrator call.
var connectionString = builder.Configuration.GetConnectionString("BugsMQ")
    ?? "Host=localhost;Port=5432;Database=bugsmq;Username=postgres;Password=postgres";
builder.Services.AddBugsMqEfCore(db => db.UseNpgsql(connectionString));

builder.Services.AddBugsMqRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
builder.Services.AddBugsMqOpenTelemetry();

builder.Services.AddSingleton<ISagaChangeNotifier, SignalRSagaChangeNotifier>();
builder.Services.AddHostedService<SagaChangePollingService>();

var app = builder.Build();

// Sample-only convenience: create the schema if it doesn't exist yet (a real deployment would use
// versioned `dotnet ef` migrations). Non-fatal if Postgres isn't reachable yet — e.g. under
// WebApplicationFactory in tests, or if this container wins the startup race against the DB — the
// app still starts; DB-backed endpoints simply fail until it's schema is created by this or another process.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<BugsMqDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not ensure the BugsMQ schema exists at startup; will retry lazily on first request.");
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors(CorsPolicy);

app.MapSagaEndpoints();
app.MapHub<SagaHub>("/hubs/saga");
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();

/// <summary>Exposed so BugsMQ.Dashboard.Api.Tests can boot the app via WebApplicationFactory.</summary>
public partial class Program;
