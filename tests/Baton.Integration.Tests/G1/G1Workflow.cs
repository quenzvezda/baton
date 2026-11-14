using System.Collections.Concurrent;
using Baton.Abstractions;

namespace Baton.Integration.Tests.G1;

// Public on purpose: the in-process agents (AgentProcess) carry this assembly's public definitions.

public sealed record G1Params(string Profile);

/// <summary>
/// The G1 load and chaos workload: <c>fetch</c> → (<c>left</c>, <c>right</c>) → <c>merge</c>, so every run
/// exercises fan-out, fan-in and DAG advance. Handlers only wait; durations are short for the load profile
/// and longer for chaos, so tasks are in flight when processes die. Retries are quick so lost leases
/// recover within the test.
/// </summary>
public sealed class G1Pipeline : WorkflowDefinition<G1Params>
{
    public override string Key => "g1-pipeline";
    public override int Version => 1;

    protected override void Define(IWorkflowBuilder<G1Params> wf)
    {
        var fetch = wf.Task<G1Work>("fetch").Retry(5, TimeSpan.FromMilliseconds(500));
        var left = wf.Task<G1Work>("left").DependsOn(fetch).Retry(5, TimeSpan.FromMilliseconds(500));
        var right = wf.Task<G1Work>("right").DependsOn(fetch).Retry(5, TimeSpan.FromMilliseconds(500));
        wf.Task<G1Work>("merge").DependsOn(left, right).Retry(5, TimeSpan.FromMilliseconds(500));
    }
}

/// <summary>
/// The next version of <see cref="G1Pipeline"/> for the rolling-upgrade test: the same graph plus a
/// <c>publish</c> task, so its hash differs. Only the "new build" agents carry it.
/// </summary>
public sealed class G1PipelineV2 : WorkflowDefinition<G1Params>
{
    public override string Key => "g1-pipeline";
    public override int Version => 2;

    protected override void Define(IWorkflowBuilder<G1Params> wf)
    {
        var fetch = wf.Task<G1Work>("fetch").Retry(5, TimeSpan.FromMilliseconds(500));
        var left = wf.Task<G1Work>("left").DependsOn(fetch).Retry(5, TimeSpan.FromMilliseconds(500));
        var right = wf.Task<G1Work>("right").DependsOn(fetch).Retry(5, TimeSpan.FromMilliseconds(500));
        var merge = wf.Task<G1Work>("merge").DependsOn(left, right).Retry(5, TimeSpan.FromMilliseconds(500));
        wf.Task<G1Work>("publish").DependsOn(merge).Retry(5, TimeSpan.FromMilliseconds(500));
    }
}

public sealed class G1Work : ITaskHandler<G1Params, int>
{
    public async Task<int> ExecuteAsync(TaskContext<G1Params> ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var (min, max) = ctx.Params.Profile == "chaos" ? (300, 3_000) : (50, 400);
        using (G1Executions.Enter(ctx.IdempotencyKey))
            await Task.Delay(Random.Shared.Next(min, max), ct);
        return ctx.Attempt;
    }
}

/// <summary>
/// Handler-side check of "no task runs twice at the same time": counts executions of the same
/// <c>runId:taskKey</c> that overlap in time inside this test process (every agent is in-process).
/// </summary>
public static class G1Executions
{
    private static readonly ConcurrentDictionary<string, int> InFlight = new();
    private static int _overlaps;
    private static int _executions;

    public static int Overlaps => Volatile.Read(ref _overlaps);

    public static int Executions => Volatile.Read(ref _executions);

    public static void Reset()
    {
        InFlight.Clear();
        Interlocked.Exchange(ref _overlaps, 0);
        Interlocked.Exchange(ref _executions, 0);
    }

    public static IDisposable Enter(string key)
    {
        Interlocked.Increment(ref _executions);
        if (InFlight.AddOrUpdate(key, 1, (_, n) => n + 1) > 1)
            Interlocked.Increment(ref _overlaps);
        return new Exit(key);
    }

    private sealed class Exit(string key) : IDisposable
    {
        public void Dispose() => InFlight.AddOrUpdate(key, 0, (_, n) => n - 1);
    }
}
