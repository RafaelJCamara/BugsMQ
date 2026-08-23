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
builder.Services.AddBugsMqOpenTelemetry();

builder.Services.AddBugsMqEngine(o => o.AddSaga<OrderSaga, OrderSagaState>());

builder.Services.AddHostedService<InventoryParticipant>();
builder.Services.AddHostedService<PaymentParticipant>();
builder.Services.AddHostedService<ShippingParticipant>();
builder.Services.AddHostedService<OrderSubmitter>();

var host = builder.Build();

// Sample-only convenience: create the schema if it doesn't exist yet. A real deployment would use
// versioned `dotnet ef` migrations instead.
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BugsMqDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await host.RunAsync();
