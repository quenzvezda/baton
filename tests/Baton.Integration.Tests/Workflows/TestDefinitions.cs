using Baton.Abstractions;
using Baton.Domain.Workflows;

namespace Baton.Integration.Tests.Workflows;

// Public on purpose: together with the linked NightlySalesReport sample and Dispatch/ChaosWorkflow (which
// the in-process agents run), these are the "definitions package" an AddWorkflows scan of this test
// assembly finds (WF-01).

public sealed record HousekeepingParams(int KeepDays = 90);

public sealed class Housekeeping : WorkflowDefinition<HousekeepingParams>
{
    public override string Key => "housekeeping";
    public override int Version => 1;

    protected override void Define(IWorkflowBuilder<HousekeepingParams> wf) => wf.Task<PurgeOldRuns>("purge-old-runs");
}

public sealed class PurgeOldRuns : ITaskHandler<HousekeepingParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<HousekeepingParams> ctx, CancellationToken ct) => Task.FromResult(0);
}

// Internal from here on, so assembly scans skip them.

internal sealed record NoParams;

/// <summary>A one- or two-task workflow under any key; <c>variant</c> changes the graph (and so the hash).</summary>
internal sealed class DynamicWorkflow(string key, int version = 1, int variant = 0, string? displayName = null)
    : WorkflowDefinition<NoParams>
{
    public override string Key => key;
    public override int Version => version;
    public override string DisplayName => displayName ?? base.DisplayName;

    public static string NewKey(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    public CompiledWorkflow Compile() => WorkflowCompiler.Compile(this);

    protected override void Define(IWorkflowBuilder<NoParams> wf)
    {
        var first = wf.Task<Noop>("first").Timeout(TimeSpan.FromMinutes(10 + variant));
        if (version > 1)
            wf.Task<Noop>("second").DependsOn(first);
    }
}

internal sealed class Noop : ITaskHandler<NoParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<NoParams> ctx, CancellationToken ct) => Task.FromResult(0);
}
