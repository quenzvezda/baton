namespace Baton.Abstractions;

/// <summary>A versioned DAG of tasks, identified by <c>key@version</c>. See design spec §5 and ADR-0004.</summary>
public interface IWorkflowDefinition
{
    /// <summary>Stable kebab-case key, e.g. <c>nightly-sales-report</c>.</summary>
    string Key { get; }

    /// <summary>Bump on any change to the graph or task options; a changed graph under the same version fails startup.</summary>
    int Version { get; }

    Type ParamsType { get; }

    void Build(IWorkflowBuilder builder);
}

public abstract class WorkflowDefinition<TParams> : IWorkflowDefinition
{
    public abstract string Key { get; }
    public abstract int Version { get; }
    public Type ParamsType => typeof(TParams);

    public void Build(IWorkflowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Define(builder.For<TParams>());
    }

    protected abstract void Define(IWorkflowBuilder<TParams> wf);
}
