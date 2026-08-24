using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Core.Dsl;

/// <summary>
/// A named state declared by an <see cref="OrchestratedSagaDefinition{TState}"/>. Purely a compile-time
/// handle for the fluent DSL — at runtime, a saga instance's current state is just the string in
/// <see cref="SagaState.CurrentState"/>, so equality here is by name.
/// </summary>
#pragma warning disable S2326 // TState is a compile-time-only marker binding State<T> to its owning saga's state type, preventing states from different sagas being mixed
public sealed class State<TState> where TState : SagaState, new()
#pragma warning restore S2326
{
    public string Name { get; }

    internal State(string name) => Name = name;

    public override string ToString() => Name;
}
