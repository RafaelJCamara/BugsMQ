using System.Linq.Expressions;
using System.Reflection;
using VSaga.Abstractions.Sagas;

namespace VSaga.Core.Dsl;

/// <summary>Shared property-selector parsing for <c>CorrelateOn(...)</c>, so the orchestrated and choreographed base classes can't drift on what counts as valid.</summary>
internal static class CorrelationSelector
{
    public static PropertyInfo ResolveProperty<TState>(Expression<Func<TState, object?>> selector, string sagaType)
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } unary ? unary.Operand : selector.Body;

        if (body is MemberExpression { Member: PropertyInfo property })
            return property;

        throw new SagaDefinitionException($"CorrelateOn selector for {sagaType} must be a simple property access, e.g. s => s.OrderId.");
    }
}
