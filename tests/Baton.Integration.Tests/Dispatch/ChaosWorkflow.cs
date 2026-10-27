using Baton.Abstractions;

namespace Baton.Integration.Tests.Dispatch;

// Public on purpose: the in-process agents (AgentProcess) carry this assembly's public definitions.

public sealed record ChaosParams(string Batch);

/// <summary>
/// extract → transform → publish. The first attempt of <c>transform</c> hangs, so the agent running it can
/// be killed mid-task; later attempts succeed. Short retry delays keep the test fast.
/// </summary>
public sealed class ChaosPipeline : WorkflowDefinition<ChaosParams>
{
    public override string Key => "chaos-pipeline";
    public override int Version => 1;

    protected override void Define(IWorkflowBuilder<ChaosParams> wf)
    {
        var extract = wf.Task<ChaosExtract>("extract");
        var transform = wf.Task<ChaosTransform>("transform").DependsOn(extract).Retry(3, TimeSpan.FromMilliseconds(200));
        wf.Task<ChaosPublish>("publish").DependsOn(transform);
    }
}

public sealed class ChaosExtract : ITaskHandler<ChaosParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<ChaosParams> ctx, CancellationToken ct) => Task.FromResult(ctx.Params.Batch.Length);
}

public sealed class ChaosTransform : ITaskHandler<ChaosParams, int>
{
    public async Task<int> ExecuteAsync(TaskContext<ChaosParams> ctx, CancellationToken ct)
    {
        if (ctx.Attempt == 1)
            await Task.Delay(Timeout.Infinite, ct);
        return ctx.GetOutput<int>("extract") * 2;
    }
}

public sealed class ChaosPublish : ITaskHandler<ChaosParams, string>
{
    public Task<string> ExecuteAsync(TaskContext<ChaosParams> ctx, CancellationToken ct) =>
        Task.FromResult($"published {ctx.GetOutput<int>("transform")}");
}
