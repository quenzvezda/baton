using Baton.Abstractions;
using Baton.Domain.Retries;

namespace Baton.Domain.Workflows;

/// <summary>One task of a <see cref="WorkflowGraph"/>, with every option resolved (defaults applied).</summary>
public sealed record TaskNode(
    string Key,
    Type HandlerType,
    IReadOnlyList<string> DependsOn,
    RetryPolicy Retry,
    TimeSpan Timeout,
    TriggerRule TriggerRule,
    IReadOnlyList<string> RequiredLabels,
    int PriorityOffset);

/// <summary>
/// What a definition's <see cref="IWorkflowDefinition.Build"/> declared, as immutable data. Tasks keep
/// declaration order; nothing is validated yet (see <see cref="WorkflowValidator"/>).
/// </summary>
public sealed record WorkflowGraph(
    string Key,
    int Version,
    string DisplayName,
    Type ParamsType,
    IReadOnlyList<TaskNode> Tasks)
{
    public string Id => $"{Key}@{Version}";

    /// <summary>Runs the definition's builder. Exceptions thrown by the definition propagate.</summary>
    public static WorkflowGraph FromDefinition(IWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var builder = new WorkflowGraphBuilder(definition.ParamsType);
        definition.Build(builder);
        return new WorkflowGraph(
            definition.Key ?? "", definition.Version, definition.DisplayName ?? "", definition.ParamsType, builder.ToNodes());
    }
}
