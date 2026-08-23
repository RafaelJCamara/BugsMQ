namespace BugsMQ.Testing;

/// <summary>Thrown by <see cref="SagaTestHarness{TDefinition,TState}"/> assertion helpers. Plain Exception subclass — no xUnit/NUnit/MSTest dependency, so it surfaces as a failure under any of them.</summary>
public sealed class SagaAssertionException(string message) : Exception(message);
