using BugsMQ.Abstractions.Sagas;

namespace BugsMQ.Core.Dsl;

/// <summary>Returned by <c>During(...)</c>; starts a new event handler for one or more states via <c>When&lt;TMessage&gt;()</c>.</summary>
public class StateBuilder<TState> where TState : SagaState, new()
{
    private protected readonly SagaDefinitionModel<TState> Model;
    private protected readonly IReadOnlyList<string> StateNames;

    internal StateBuilder(SagaDefinitionModel<TState> model, IReadOnlyList<string> stateNames)
    {
        Model = model;
        StateNames = stateNames;
    }

    public EventBuilder<TState, TMessage> When<TMessage>() where TMessage : notnull
    {
        var step = new StepDefinition<TState>(typeof(TMessage));
        foreach (var stateName in StateNames)
            Model.AddStep(stateName, step);

        return new EventBuilder<TState, TMessage>(Model, StateNames, step);
    }
}
