using System.Text.Json;
using System.Text.Json.Serialization;
using VSaga.Abstractions.Notifications;
using VSaga.Dashboard.Api;
using VSaga.Dashboard.Api.Auth;
using VSaga.Dashboard.Api.Endpoints;
using VSaga.Dashboard.Api.HealthChecks;
using VSaga.Dashboard.Api.Hubs;
using VSaga.Observability;
using VSaga.Persistence.EFCore;
using VSaga.Transport.RabbitMQ;
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

// Dashboard.Api deliberately never calls AddVSagaEngine/AddSaga<>() — it stays generic across any
// number of saga types by reading persisted data (ISagaSummaryReader/ISagaEventLogStore) rather than
// requiring its own copy of every saga definition. Retry works the same way: it redrives via a raw
// transport republish (see SagaEndpoints), not an in-process orchestrator call.
var connectionString = builder.Configuration.GetConnectionString("VSaga")
    ?? "Host=localhost;Port=5432;Database=vsaga;Username=postgres;Password=postgres";
// Migrations live in VSaga.Persistence.EFCore.Postgres (not VSaga.Persistence.EFCore) so the latter
// can stay free of any Npgsql-specific reference/generated code — it only depends on
// Microsoft.EntityFrameworkCore, not any specific provider. MigrationsAssembly points EF Core at the
// Postgres project's assembly instead of the DbContext's own.
builder.Services.AddVSagaEfCore(db => db.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")));

builder.Services.AddVSagaRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
builder.Services.AddVSagaOpenTelemetry();

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
        var db = scope.ServiceProvider.GetRequiredService<VSagaDbContext>();
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not apply VSaga migrations at startup; will retry lazily on first request.");
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

/// <summary>Exposed so VSaga.Dashboard.Api.Tests can boot the app via WebApplicationFactory.</summary>
#pragma warning disable S1118 // required marker type for WebApplicationFactory<Program>, not a utility class
public partial class Program;
#pragma warning restore S1118
