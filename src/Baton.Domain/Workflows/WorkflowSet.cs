using Baton.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Domain.Workflows;

/// <summary>
/// Every definition registered with <see cref="WorkflowServiceCollectionExtensions.AddWorkflows"/>,
/// compiled. Resolving it runs the startup checks: it throws one <see cref="WorkflowDefinitionException"/>
/// listing the problems of all definitions, so a broken definition stops the process (WF-03).
/// </summary>
public sealed class WorkflowSet
{
    public WorkflowSet(IEnumerable<IWorkflowDefinition> definitions, IServiceProviderIsService services)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var compiled = new List<CompiledWorkflow>();
        var errors = new List<string>();
        foreach (var definition in definitions)
        {
            try
            {
                compiled.Add(WorkflowCompiler.Compile(definition, services));
            }
            catch (WorkflowDefinitionException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }

        foreach (var duplicate in compiled.GroupBy(w => w.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var types = string.Join(", ", duplicate.Select(w => w.Definition.GetType().FullName));
            errors.Add($"{duplicate.Key}: declared by more than one definition ({types})");
        }

        if (errors.Count > 0)
            throw new WorkflowDefinitionException(errors);

        Workflows = [.. compiled.OrderBy(w => w.Key, StringComparer.Ordinal).ThenBy(w => w.Version)];
    }

    public IReadOnlyList<CompiledWorkflow> Workflows { get; }
}
