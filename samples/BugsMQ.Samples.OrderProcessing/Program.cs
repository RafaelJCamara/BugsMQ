using BugsMQ.Chaos;
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

// Opt-in only (default off, see appsettings.json/docker-compose.chaos.yml): registration order
// relative to AddBugsMqRabbitMq below doesn't matter — MiddlewarePipelineTransport only resolves
// registered IOutboundMessageMiddleware/IInboundMessageMiddleware lazily, once IMessageTransport is
// first requested, by which point every AddXyz call here has already run.
if (builder.Configuration.GetValue("Chaos:Enabled", defaultValue: false))
    builder.Services.AddBugsMqChaos(o => builder.Configuration.GetSection("Chaos").Bind(o));

builder.Services.AddBugsMqRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
// Wraps the transport so every SubscribeAsync call (the saga engine's and the participants' alike)
// records which service consumes which message type — the only way the saga map can name a
// destination that never actually replied (e.g. the simulated hung payment gateway).
builder.Services.AddBugsMqTopologyRecording();
builder.Services.AddBugsMqOpenTelemetry();

// Both saga kinds in one engine. They deliberately share a correlation id per order — OrderSaga
// drives the order to shipment, PostShipmentChoreography tracks the independent fan-out that follows
// — which is only expressible because a saga instance is keyed by (SagaType, CorrelationId). Both
// receive their own copy of OrderShipped: the RabbitMQ transport binds one queue per subscription to
// a topic exchange, so a published message reaches every subscriber of that type rather than one.
builder.Services.AddBugsMqEngine(o => o
    .AddSaga<OrderSaga, OrderSagaState>()
    .AddSaga<PostShipmentChoreography, PostShipmentState>());

builder.Services.AddHostedService<InventoryParticipant>();
builder.Services.AddHostedService<PaymentParticipant>();
builder.Services.AddHostedService<ShippingParticipant>();

// The choreographed leg's participants: no conductor commands these, they each react to OrderShipped.
builder.Services.AddHostedService<NotificationParticipant>();
builder.Services.AddHostedService<LoyaltyParticipant>();
builder.Services.AddHostedService<InvoicingParticipant>();

builder.Services.AddHostedService<OrderSubmitter>();

var host = builder.Build();

// Schema creation is dashboard-api's job (versioned `dotnet ef` migrations against
// BugsMQ.Persistence.EFCore.Postgres) — docker-compose.yml gates this service on dashboard-api's
// health check so its migration always runs first. This service used to also call
// EnsureCreatedAsync() as a standalone-convenience fallback, but that bypasses
// __EFMigrationsHistory: run against a database it bootstrapped, a later `dotnet ef migrations add`
// migration fails with "relation already exists". Removed rather than kept as a landmine.
await host.RunAsync();
