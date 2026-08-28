# VSaga.Testing

`SagaTestHarness`: an in-process test harness for vSaga saga definitions, with no test-framework
dependency of its own — runs under xUnit, NUnit, MSTest, or straight from `Program.cs`. Exercises the
real engine against the in-memory transport/persistence providers, no broker or database required.

## Install

```bash
dotnet add package VSaga.Testing
```

## Usage

```csharp
await using var harness = new SagaTestHarness<OrderApprovalSaga, OrderApprovalState>();

await harness.Given(Guid.NewGuid()).WhenAsync(new SubmitOrder(Guid.NewGuid(), Amount: 250m));

await harness.AssertStatusAsync(SagaStatus.Completed);
harness.AssertPublished<OrderApproved>();
```

## Docs

[docs/testing.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/testing.md) for the full
harness API, including `AdvanceTimeByAsync` for deterministic timeout tests.

## License

MIT
