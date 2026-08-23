using BugsMQ.Abstractions.Notifications;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BugsMQ.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BugsMQ saga engine: the hosted service that subscribes registered sagas to the
    /// configured <c>IMessageTransport</c>, the timeout dispatcher, and (if nothing else registered
    /// one already) a no-op <see cref="ISagaChangeNotifier"/>. Requires an <c>IMessageTransport</c>
    /// and, per registered saga TState, an <c>ISagaSnapshotStore&lt;TState&gt;</c> to already be
    /// registered (e.g. via AddBugsMqInMemoryPersistence/AddBugsMqEfCore and AddBugsMqInMemoryTransport/AddBugsMqRabbitMq).
    /// </summary>
    public static IServiceCollection AddBugsMqEngine(this IServiceCollection services, Action<SagaEngineBuilder> configure)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISagaChangeNotifier>(NullSagaChangeNotifier.Instance);
        services.AddSingleton<ISagaRetryDispatcher, SagaRetryDispatcher>();
        services.AddHostedService<SagaEngineHostedService>();
        services.AddHostedService<SagaTimeoutDispatcherHostedService>();

        configure(new SagaEngineBuilder(services));

        return services;
    }
}

public sealed class SagaEngineBuilder(IServiceCollection services)
{
    /// <summary>
    /// Registers a saga definition (a singleton, stateless DSL/model shared across all of that saga
    /// type's instances) and wires it into the engine. <see cref="SagaOrchestrator{TState}"/> is
    /// registered Scoped — not Singleton — because it depends on persistence stores that may need a
    /// short-lived unit of work (e.g. EF Core's DbContext); <see cref="SagaRuntime{TState}"/> opens a
    /// fresh scope per message/timeout/retry so a Singleton hosted service can still drive it safely.
    /// </summary>
    public SagaEngineBuilder AddSaga<TDefinition, TState>()
        where TDefinition : class, ISagaDefinition<TState>
        where TState : SagaState, new()
    {
        services.AddSingleton<TDefinition>();
        services.AddSingleton<ISagaDefinition<TState>>(sp => sp.GetRequiredService<TDefinition>());
        services.AddScoped<SagaOrchestrator<TState>>();
        services.AddSingleton<ISagaRuntime, SagaRuntime<TState>>();

        return this;
    }
}
