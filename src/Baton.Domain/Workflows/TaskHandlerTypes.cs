using Baton.Abstractions;

namespace Baton.Domain.Workflows;

internal static class TaskHandlerTypes
{
    /// <summary>The <c>ITaskHandler&lt;TParams, TOutput&gt;</c> interfaces a type implements.</summary>
    public static IEnumerable<Type> HandlerInterfaces(Type type) =>
        type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITaskHandler<,>));

    public static bool IsHandler(Type type) => HandlerInterfaces(type).Any();
}
