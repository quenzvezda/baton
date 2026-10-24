using System.Reflection;
using Baton.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Baton.Domain.Workflows;

public static class WorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers every public, concrete <see cref="IWorkflowDefinition"/> (singleton) and
    /// <c>ITaskHandler&lt;TParams, TOutput&gt;</c> (scoped, resolved per attempt) in the assemblies, plus
    /// <see cref="WorkflowSet"/>. Non-public types are skipped, so test or helper types stay out.
    /// </summary>
    public static IServiceCollection AddWorkflows(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var types = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                        && (t.IsPublic || t.IsNestedPublic));

        foreach (var type in types)
        {
            if (typeof(IWorkflowDefinition).IsAssignableFrom(type))
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IWorkflowDefinition), type));

            if (TaskHandlerTypes.IsHandler(type))
                services.TryAddScoped(type);
        }

        services.TryAddSingleton<WorkflowSet>();
        return services;
    }
}
