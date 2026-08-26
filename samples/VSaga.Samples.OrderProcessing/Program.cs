using VSaga.Chaos;
using VSaga.Core;
using VSaga.Observability;
using VSaga.Persistence.EFCore;
using VSaga.Samples.OrderProcessing;
using VSaga.Samples.OrderProcessing.Participants;
using VSaga.Transport.RabbitMQ;
using VSaga.Transport.MassTransit;
using VSaga.Transport.Brighter;
using VSaga.Transport.Wolverine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("VSaga")
    ?? "Host=localhost;Port=5432;Database=vsaga;Username=postgres;Password=postgres";
builder.Services.AddVSagaEfCore(db => db.UseNpgsql(connectionString));

// Opt-in only (default off, see appsettings.json/docker-compose.chaos.yml): registration order
// relative to AddVSagaRabbitMq below doesn't matter — MiddlewarePipelineTransport only resolves
// registered IOutboundMessageMiddleware/IInboundMessageMiddleware lazily, once IMessageTransport is
// first requested, by which point every AddXyz call here has already run.
if (builder.Configuration.GetValue("Chaos:Enabled", defaultValue: false))
    builder.Services.AddVSagaChaos(o => builder.Configuration.GetSection("Chaos").Bind(o));

// Transport:Provider picks which IMessageTransport adapter this sample runs against — RabbitMQ by
// default (matching every prior compose run), or one of the other adapters via a docker-compose
// overlay (e.g. docker-compose.wolverine.yml sets Transport__Provider=Wolverine) for that adapter's
// own live-verification pass. Each adapter's own ServiceCollectionExtensions wraps its transport in
// the same MiddlewarePipelineTransport as RabbitMQ, so chaos/topology-recording work unchanged.
switch (builder.Configuration["Transport:Provider"] ?? "RabbitMq")
{
    case "RabbitMq":
        builder.Services.AddVSagaRabbitMq(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
        break;
    case "Wolverine":
        builder.Services.AddVSagaWolverine(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
        break;
    case "MassTransit":
        builder.Services.AddVSagaMassTransit(o => builder.Configuration.GetSection("MassTransit").Bind(o));
        break;
    case "Brighter":
        // Binds from the same "RabbitMq" section RabbitMqOptions uses — BrighterOptions mirrors
        // RabbitMqOptions' shape closely enough that this is the least-surprising way to keep the two
        // adapters config-swappable; docker-compose.brighter.yml only overrides Transport:Provider.
        builder.Services.AddVSagaBrighter(o => builder.Configuration.GetSection("RabbitMq").Bind(o));
        break;
    default:
        throw new InvalidOperationException($"Unknown Transport:Provider '{builder.Configuration["Transport:Provider"]}'.");
}
// Wraps the transport so every SubscribeAsync call (the saga engine's and the participants' alike)
// records which service consumes which message type — the only way the saga map can name a
// destination that never actually replied (e.g. the simulated hung payment gateway).
builder.Services.AddVSagaTopologyRecording();
builder.Services.AddVSagaOpenTelemetry();

// Both saga kinds in one engine. They deliberately share a correlation id per order — OrderSaga
// drives the order to shipment, PostShipmentChoreography tracks the independent fan-out that follows
// — which is only expressible because a saga instance is keyed by (SagaType, CorrelationId). Both
// receive their own copy of OrderShipped: the RabbitMQ transport binds one queue per subscription to
// a topic exchange, so a published message reaches every subscriber of that type rather than one.
//
// InvoiceDeliverySaga and InvoiceArchivalSaga are the odd ones out: they do NOT share that correlation
// id. Each is a sub-saga started with ctx.StartChildAsync — by PostShipmentChoreography and
// InvoiceFollowUpSaga respectively — so each gets a fresh id of its own plus a stored pointer back to
// the instance that started it. Registering them here is all the wiring there is — the parent publishes
// DeliverInvoice/ArchiveInvoice and the child's CanInitiate matches it; neither type references the
// other. InvoiceFollowUpSaga itself DOES share the order's correlation id, same as
// PostShipmentChoreography: both react to InvoiceIssued, so both open under whatever correlation id
// that message already carries.
builder.Services.AddVSagaEngine(o => o
    .AddSaga<OrderSaga, OrderSagaState>()
    .AddSaga<PostShipmentChoreography, PostShipmentState>()
    .AddSaga<InvoiceDeliverySaga, InvoiceDeliveryState>()
    .AddSaga<InvoiceFollowUpSaga, InvoiceFollowUpState>()
    .AddSaga<InvoiceArchivalSaga, InvoiceArchivalState>());

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
// VSaga.Persistence.EFCore.Postgres) — docker-compose.yml gates this service on dashboard-api's
// health check so its migration always runs first. This service used to also call
// EnsureCreatedAsync() as a standalone-convenience fallback, but that bypasses
// __EFMigrationsHistory: run against a database it bootstrapped, a later `dotnet ef migrations add`
// migration fails with "relation already exists". Removed rather than kept as a landmine.
await host.RunAsync();
