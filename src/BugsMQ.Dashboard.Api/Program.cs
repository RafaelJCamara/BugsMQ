using System.Text.Json;
using System.Text.Json.Serialization;
using BugsMQ.Abstractions.Notifications;
using BugsMQ.Dashboard.Api;
using BugsMQ.Dashboard.Api.Auth;
using BugsMQ.Dashboard.Api.Endpoints;
using BugsMQ.Dashboard.Api.HealthChecks;
using BugsMQ.Dashboard.Api.Hubs;
using BugsMQ.Observability;
using BugsMQ.Persistence.EFCore;
using BugsMQ.Transport.RabbitMQ;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

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
// Migrations live in BugsMQ.Persistence.EFCore.Postgres (not BugsMQ.Persistence.EFCore) so the latter
// can stay free of any Npgsql-specific reference/generated code — it only depends on
// Microsoft.EntityFrameworkCore, not any specific provider. MigrationsAssembly points EF Core at the
// Postgres project's assembly instead of the DbContext's own.
builder.Services.AddBugsMqEfCore(db => db.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("BugsMQ.Persistence.EFCore.Postgres")));

builder.Services.AddBugsMqRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
builder.Services.AddBugsMqOpenTelemetry();

builder.Services.AddSingleton<ISagaChangeNotifier, SignalRSagaChangeNotifier>();
builder.Services.AddHostedService<SagaChangePollingService>();

builder.Services.AddHealthChecks()
    .AddCheck<PostgresHealthCheck>("postgres")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

builder.Services.AddAuthentication(ApiKeyAuthenticationDefaults.SchemeName)
    .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationDefaults.SchemeName, configureOptions: null);
builder.Services.AddAuthorization();

var app = builder.Build();

// Apply versioned EF Core migrations at startup. Non-fatal if Postgres isn't reachable yet — e.g.
// under WebApplicationFactory in tests, or if this container wins the startup race against the DB —
// the app still starts; DB-backed endpoints simply fail until the schema is migrated by this or
// another process.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<BugsMqDbContext>();
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not apply BugsMQ migrations at startup; will retry lazily on first request.");
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors(CorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapSagaEndpoints();
app.MapHub<SagaHub>("/hubs/saga").RequireAuthorization();
// Left unauthenticated: infra probes (docker-compose healthcheck, orchestrators) hit this without a key.
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthResponseAsync });

await app.RunAsync();

// Preserves the endpoint's original { "status": "healthy" } response shape (extended with a per-check
// breakdown) instead of the health-checks middleware's default plain-text body.
static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";

    var payload = new
    {
        status = ToStatusString(report.Status),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = ToStatusString(e.Value.Status),
            description = e.Value.Description,
        }),
    };

    return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

static string ToStatusString(HealthStatus status) => status switch
{
    HealthStatus.Healthy => "healthy",
    HealthStatus.Degraded => "degraded",
    _ => "unhealthy",
};

/// <summary>Exposed so BugsMQ.Dashboard.Api.Tests can boot the app via WebApplicationFactory.</summary>
#pragma warning disable S1118 // required marker type for WebApplicationFactory<Program>, not a utility class
public partial class Program;
#pragma warning restore S1118
