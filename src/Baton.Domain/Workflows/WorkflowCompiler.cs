using Baton.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Domain.Workflows;

/// <summary>A definition that passed every startup check, with what registration stores.</summary>
public sealed record CompiledWorkflow(
    IWorkflowDefinition Definition,
    WorkflowGraph Graph,
    string CanonicalJson,
    string Hash,
    string ParamsSchema)
{
    public string Key => Graph.Key;
    public int Version => Graph.Version;
    public string DisplayName => Graph.DisplayName;
    public string Id => Graph.Id;
}

/// <summary>Builds, validates and hashes definitions — everything startup checks before registration (WF-03).</summary>
public static class WorkflowCompiler
{
    /// <param name="services">When given, every handler must be registered in DI.</param>
    /// <exception cref="WorkflowDefinitionException">The definition is invalid; every problem is listed.</exception>
    public static CompiledWorkflow Compile(IWorkflowDefinition definition, IServiceProviderIsService? services = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var id = $"{definition.Key}@{definition.Version}";
        WorkflowGraph graph;
        try
        {
            graph = WorkflowGraph.FromDefinition(definition);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new WorkflowDefinitionException([$"{id}: Build threw {ex.GetType().Name}: {ex.Message}"], ex);
        }

        var errors = new List<string>(WorkflowValidator.Validate(graph));
        if (services is not null)
        {
            errors.AddRange(graph.Tasks
                .Select(t => t.HandlerType)
                .Distinct()
                .Where(h => !services.IsService(h))
                .Select(h => $"handler '{h}' is not registered in DI (AddWorkflows registers public handlers of the scanned assemblies)"));
        }

        var schema = WorkflowJson.TryExportParamsSchema(graph.ParamsType, out var schemaError);
        if (schemaError is not null)
            errors.Add(schemaError);

        if (errors.Count > 0)
            throw new WorkflowDefinitionException([.. errors.Select(e => $"{id}: {e}")]);

        var json = CanonicalGraph.ToJson(graph);
        return new CompiledWorkflow(definition, graph, json, CanonicalGraph.Hash(json), schema!);
    }
}
