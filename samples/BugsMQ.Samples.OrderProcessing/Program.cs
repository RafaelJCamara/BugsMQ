using BugsMQ.Core;
using BugsMQ.Observability;
using BugsMQ.Persistence.EFCore;
using BugsMQ.Samples.OrderProcessing;
using BugsMQ.Samples.OrderProcessing.Participants;
using BugsMQ.Transport.RabbitMQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("BugsMQ")
    ?? "Host=localhost;Port=5432;Database=bugsmq;Username=postgres;Password=postgres";
builder.Services.AddBugsMqEfCore(db => db.UseNpgsql(connectionString));

builder.Services.AddBugsMqRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
// Wraps the transport so every SubscribeAsync call (the saga engine's and the participants' alike)
// records which service consumes which message type — the only way the saga map can name a
// destination that never actually replied (e.g. the simulated hung payment gateway).
builder.Services.AddBugsMqTopologyRecording();
builder.Services.AddBugsMqOpenTelemetry();

builder.Services.AddBugsMqEngine(o => o.AddSaga<OrderSaga, OrderSagaState>());

builder.Services.AddHostedService<InventoryParticipant>();
builder.Services.AddHostedService<PaymentParticipant>();
builder.Services.AddHostedService<ShippingParticipant>();
builder.Services.AddHostedService<OrderSubmitter>();

var host = builder.Build();

// Schema creation is dashboard-api's job (versioned `dotnet ef` migrations against
// BugsMQ.Persistence.EFCore.Postgres) — docker-compose.yml gates this service on dashboard-api's
// health check so its migration always runs first. This service used to also call
// EnsureCreatedAsync() as a standalone-convenience fallback, but that bypasses
// __EFMigrationsHistory: run against a database it bootstrapped, a later `dotnet ef migrations add`
// migration fails with "relation already exists". Removed rather than kept as a landmine.
await host.RunAsync();
