using Baton.Abstractions;

namespace Baton.Domain.Tests.Workflows;

// Internal on purpose: AddWorkflows scans public types only, so these never leak into an assembly scan.

internal sealed record NoParams;

/// <summary>A definition whose graph is supplied by the test.</summary>
internal sealed class InlineWorkflow<TParams>(
    Action<IWorkflowBuilder<TParams>> define,
    string key = "test-workflow",
    int version = 1,
    string? displayName = null) : WorkflowDefinition<TParams>
{
    public override string Key => key;
    public override int Version => version;
    public override string DisplayName => displayName ?? base.DisplayName;

    protected override void Define(IWorkflowBuilder<TParams> wf) => define(wf);
}

internal static class InlineWorkflow
{
    public static InlineWorkflow<NoParams> Of(Action<IWorkflowBuilder<NoParams>> define, string key = "test-workflow") =>
        new(define, key);
}

internal sealed class StepA : ITaskHandler<NoParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<NoParams> ctx, CancellationToken ct) => Task.FromResult(1);
}

internal sealed class StepB : ITaskHandler<NoParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<NoParams> ctx, CancellationToken ct) => Task.FromResult(2);
}
