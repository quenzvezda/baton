using Baton.Abstractions;
using Microsoft.Extensions.Logging;

namespace Baton.Agent.Tests;

// Public on purpose: AddBatonAgent scans this assembly for the definitions the fake orchestrator leases.

public sealed record AgentTestParams(string Value);

/// <summary>One root task per behavior the executor must handle.</summary>
public sealed class AgentTestWorkflow : WorkflowDefinition<AgentTestParams>
{
    public override string Key => "agent-test";
    public override int Version => 1;

    protected override void Define(IWorkflowBuilder<AgentTestParams> wf)
    {
        var echo = wf.Task<Echo>("echo");
        wf.Task<ReadUpstream>("read-upstream").DependsOn(echo);
        wf.Task<Fail>("fail");
        wf.Task<FailFatally>("fail-fatally");
        wf.Task<WaitForCancellation>("wait");
        wf.Task<ReturnTooMuch>("too-much");
        wf.Task<WriteLogs>("write-logs");
    }
}

public sealed class Echo : ITaskHandler<AgentTestParams, string>
{
    public Task<string> ExecuteAsync(TaskContext<AgentTestParams> ctx, CancellationToken ct) =>
        Task.FromResult($"{ctx.Params.Value}#{ctx.Attempt}");
}

public sealed class ReadUpstream : ITaskHandler<AgentTestParams, string>
{
    public Task<string> ExecuteAsync(TaskContext<AgentTestParams> ctx, CancellationToken ct) =>
        Task.FromResult(ctx.GetOutput<string>("echo") + "!");
}

public sealed class Fail : ITaskHandler<AgentTestParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<AgentTestParams> ctx, CancellationToken ct) =>
        throw new InvalidOperationException("transient trouble");
}

public sealed class FailFatally : ITaskHandler<AgentTestParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<AgentTestParams> ctx, CancellationToken ct) =>
        throw new NonRetryableException("bad input");
}

public sealed class WaitForCancellation : ITaskHandler<AgentTestParams, int>
{
    public async Task<int> ExecuteAsync(TaskContext<AgentTestParams> ctx, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }
}

public sealed class ReturnTooMuch : ITaskHandler<AgentTestParams, string>
{
    public Task<string> ExecuteAsync(TaskContext<AgentTestParams> ctx, CancellationToken ct) =>
        Task.FromResult(new string('x', 300 * 1024));
}

public sealed class WriteLogs : ITaskHandler<AgentTestParams, int>
{
    public Task<int> ExecuteAsync(TaskContext<AgentTestParams> ctx, CancellationToken ct)
    {
        for (var i = 1; i <= 1200; i++)
            ctx.Logger.Log(LogLevel.Information, new EventId(i), i, null, (n, _) => $"line {n}");
        return Task.FromResult(1200);
    }
}
