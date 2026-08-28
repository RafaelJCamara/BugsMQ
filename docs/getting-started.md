# Getting started

This page installs vSaga and writes a small, real saga end to end, running in-process against the
in-memory transport and persistence providers — no Docker, no database, no broker required. For
running the full reference stack (Postgres, RabbitMQ, the dashboard) instead, see
["Run the demo"](../README.md#run-the-demo) in the root README.

> vSaga has not yet cut a tagged release, so the packages below aren't on nuget.org/npm yet. Reference
> them as project references (`dotnet add reference ../path/to/VSaga.Core/VSaga.Core.csproj`) or via a
> local `dotnet pack`/`npm pack` until the first release ships — the commands below are the shape usage
> will take once it has.

## Install

```bash
dotnet new console -n MyFirstSaga && cd MyFirstSaga
dotnet add package VSaga.Core
dotnet add package VSaga.Persistence.InMemory
dotnet add package VSaga.Transport.InMemory
dotnet add package Microsoft.Extensions.Hosting
```

(Swap `VSaga.Persistence.InMemory`/`VSaga.Transport.InMemory` for `VSaga.Persistence.EFCore` +
`VSaga.Persistence.EFCore.Postgres` + `VSaga.Transport.RabbitMQ` when you're ready for something that
survives a restart — see [`persistence.md`](persistence.md) and [`transports/index.md`](transports/index.md).)

## Write a saga

A saga has three parts: the messages it reacts to, the state it accumulates, and the definition that
wires the two together. This one approves an order automatically and reports the result — small
enough to read in one sitting, real enough to show the actual DSL.

```csharp
// Messages.cs
public sealed record SubmitOrder(Guid OrderId, decimal Amount);
public sealed record OrderApproved(Guid OrderId);
public sealed record OrderRejected(Guid OrderId, string Reason);
```

```csharp
// OrderApprovalState.cs
using VSaga.Abstractions.Sagas;

public sealed class OrderApprovalState : SagaState
{
    public decimal Amount { get; set; }
}
```

```csharp
// OrderApprovalSaga.cs
using VSaga.Abstractions.Sagas;
using VSaga.Core.Dsl;

public sealed class OrderApprovalSaga : OrchestratedSagaDefinition<OrderApprovalState>
{
    public State<OrderApprovalState> AwaitingApproval { get; }
    public State<OrderApprovalState> Approved { get; }

    public OrderApprovalSaga()
    {
        AwaitingApproval = InitialState(nameof(AwaitingApproval));
        Approved = State(nameof(Approved));

        During(AwaitingApproval)
            .When<SubmitOrder>()
                .Then((ctx, msg) => ctx.Saga.Amount = msg.Amount)
                // A step's own business rule -- anything over 10,000 needs a human, everything else
                // auto-approves. A real saga would call a participant here instead; see
                // docs/saga-dsl.md for .Publish(...) and .CallHttp(...).
                .Then((ctx, msg) =>
                {
                    if (msg.Amount > 10_000m)
                        throw new InvalidOperationException("Manual approval not implemented in this example.");
                })
                .Publish((ctx, msg) => new OrderApproved(msg.OrderId))
                .TransitionTo(Approved)
                .Finalize(SagaStatus.Completed);
    }
}
```

`InitialState(...)` declares the state a brand-new instance starts in; `During(state).When<T>()` gates
a step to that state; `.Then(...)` runs your logic; `.Publish(...)` sends a message onward;
`.TransitionTo(...)` moves the saga forward; `.Finalize(...)` marks it terminal. See
[`saga-dsl.md`](saga-dsl.md) for the complete method reference and
[`concepts.md`](concepts.md) for compensation, timeouts, and the choreographed alternative.

## Run it

```csharp
// Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VSaga.Abstractions.Transport;
using VSaga.Core;
using VSaga.Persistence.InMemory;
using VSaga.Transport.InMemory;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddVSagaInMemoryPersistence();
builder.Services.AddVSagaInMemoryTransport();
builder.Services.AddVSagaEngine(o => o.AddSaga<OrderApprovalSaga, OrderApprovalState>());

using var host = builder.Build();
await host.StartAsync();

var transport = host.Services.GetRequiredService<IMessageTransport>();
var orderId = Guid.NewGuid();

await transport.PublishAsync(
    new SubmitOrder(orderId, Amount: 250m),
    MessageEnvelope.New(orderId));   // the saga's correlation id -- see concepts.md

await Task.Delay(100);   // the in-memory transport dispatches synchronously, but give the host a beat
await host.StopAsync();
```

Run it with `dotnet run`. There's no console output yet because nothing in the saga logs anything —
that's expected for this minimal example. To actually observe the result, either add a
`.Then((ctx, msg) => Console.WriteLine($"Order {msg.OrderId} approved"))` to the `OrderApproved`
handler of a second participant saga, or read the persisted state back directly:

```csharp
using VSaga.Abstractions.Persistence;

var store = host.Services.GetRequiredService<ISagaSnapshotStore<OrderApprovalState>>();
var state = await store.FindAsync(nameof(OrderApprovalSaga), orderId, CancellationToken.None);
Console.WriteLine($"Status: {state?.Status}, State: {state?.CurrentState}");
// Status: Completed, State: Approved
```

## Test it

`VSaga.Testing`'s `SagaTestHarness` runs the same saga against the real engine without a host at all —
the natural way to unit-test a saga definition:

```csharp
await using var harness = new SagaTestHarness<OrderApprovalSaga, OrderApprovalState>();

await harness.Given(Guid.NewGuid()).WhenAsync(new SubmitOrder(Guid.NewGuid(), Amount: 250m));

await harness.AssertStatusAsync(SagaStatus.Completed);
harness.AssertPublished<OrderApproved>();
```

See [`testing.md`](testing.md) for the full harness API.

## Where to go next

- [`concepts.md`](concepts.md) — orchestrated vs. choreographed, correlation, compensation, timeouts.
- [`saga-dsl.md`](saga-dsl.md) — every DSL method, including `.CallHttp` for calling a plain REST API
  from a step.
- [`configuration.md`](configuration.md) — the outbox, transport options, and everything else
  configurable.
- [`persistence.md`](persistence.md) and [`transports/index.md`](transports/index.md) — moving from
  in-memory to EF Core/Postgres and a real broker.
- [`dashboard.md`](dashboard.md) — the ops dashboard, once you have Postgres/RabbitMQ running — see
  ["Run the demo"](../README.md#run-the-demo) for the fastest way to see it live.
