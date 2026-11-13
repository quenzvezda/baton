using System.Globalization;
using System.Text;
using Baton.Orchestrator.Dispatch;
using Xunit.Abstractions;

namespace Baton.Integration.Tests.G1;

/// <summary>
/// Gate G1 Resilience (OQ-01: Postgres restart instead of failover, 2026-11-14). Under continuous load: kill an
/// agent mid-task, kill one of two orchestrator replicas, restart Postgres; then a rolling agent upgrade. Every
/// accepted run must succeed, no task may hold two leases at once, no task may have two accepted results.
/// Runs by hand (<c>--filter Category=G1</c>).
/// </summary>
public sealed class G1ChaosTests(ITestOutputHelper output)
{
    private const double RunsPerSecond = 2;

    private static readonly Action<DispatchOptions> FastRecovery = d =>
    {
        d.ReaperInterval = TimeSpan.FromSeconds(1);
        d.RetryPromoterInterval = TimeSpan.FromMilliseconds(500);
        d.ClaimRecheck = TimeSpan.FromMilliseconds(500);
    };

    [Fact]
    [Trait("Category", "G1")]
    public async Task Agent_orchestrator_and_postgres_failures_under_load_lose_no_run_and_never_run_a_task_twice_at_once()
    {
        G1Executions.Reset();
        // 30 s leases (the smallest a group allows) outlast the 10 s Postgres outage, as production leases must.
        // 24 slots for ~13 busy on average, so capacity stays ahead of demand after the agent dies.
        await using var rig = await G1Rig.StartAsync(orchestrators: 2, agents: 8, slots: 3, leaseSeconds: 30, profile: "chaos", FastRecovery);
        var timeline = new G1Rig.Timeline();
        using var stop = new CancellationTokenSource();
        timeline.Mark($"producer starts: {RunsPerSecond} runs/s × 4 tasks of 0.3–3 s");
        var producing = rig.ProduceAsync(RunsPerSecond, stop.Token);

        await WaitUntilAsync(timeline, TimeSpan.FromSeconds(30));
        // The busiest agent, so tasks are really cut mid-flight.
        var (victim, held) = (rig.Agents[0], -1L);
        foreach (var agent in rig.Agents)
        {
            var running = await HostDataRunningOnAsync(rig, agent.Name);
            if (running > held)
                (victim, held) = (agent, running);
        }

        victim.Kill();
        timeline.Mark($"agent {victim.Name} killed (network cut) holding {held} running tasks");

        await WaitUntilAsync(timeline, TimeSpan.FromSeconds(75));
        await rig.KillOrchestratorAsync(1);
        timeline.Mark("orchestrator replica 2 of 2 stopped; its agents fail over to replica 1");

        await WaitUntilAsync(timeline, TimeSpan.FromSeconds(120));
        timeline.Mark("Postgres stopped");
        await rig.Pg.RestartAsync(downtime: TimeSpan.FromSeconds(10));
        timeline.Mark("Postgres started again");

        await WaitUntilAsync(timeline, TimeSpan.FromSeconds(180));
        await stop.CancelAsync();
        await producing;
        timeline.Mark("producer stops");
        await rig.DrainAsync(TimeSpan.FromMinutes(4));
        timeline.Mark("every run final");

        var m = await rig.MeasureAsync();
        await WriteReportAsync("g1-chaos-report.md", "G1 chaos: 2 orchestrator replicas, 8 agents × 3 slots, lease 30 s", m, timeline);

        Assert.Equal(0, m.AcceptedNotSucceeded);
        Assert.True(m.LostAttempts >= held, $"The killed agent's {held} tasks were not reaped ({m.LostAttempts} lost attempts).");
        Assert.Equal(0, m.LeaseOverlaps);
        Assert.Equal(0, m.DoubleSuccesses);
        Assert.Equal(0, m.HandlerOverlaps);
    }

    [Fact]
    [Trait("Category", "G1")]
    public async Task Rolling_agent_upgrade_under_load_fails_no_run()
    {
        G1Executions.Reset();
        await using var rig = await G1Rig.StartAsync(orchestrators: 2, agents: 6, slots: 3, leaseSeconds: 30, profile: "chaos", FastRecovery);
        var timeline = new G1Rig.Timeline();
        using var stop = new CancellationTokenSource();
        timeline.Mark($"producer starts: {RunsPerSecond} runs/s on 6 old agents");
        var producing = rig.ProduceAsync(RunsPerSecond, stop.Token);

        await WaitUntilAsync(timeline, TimeSpan.FromSeconds(20));
        var old = rig.Agents.ToList();
        for (var i = 0; i < old.Count; i++)
            await rig.AddAgentAsync(slots: 3, preferred: i);
        timeline.Mark($"{old.Count} new agents started");

        foreach (var agent in old)
        {
            await WaitUntilAsync(timeline, timeline.Elapsed + TimeSpan.FromSeconds(10));
            var running = await HostDataRunningOnAsync(rig, agent.Name);
            await agent.StopAsync(TimeSpan.FromSeconds(30)); // drain: no new claims, running tasks finish and report
            timeline.Mark($"old agent {agent.Name} drained and stopped ({running} tasks were running)");
        }

        await WaitUntilAsync(timeline, timeline.Elapsed + TimeSpan.FromSeconds(20));
        await stop.CancelAsync();
        await producing;
        timeline.Mark("producer stops");
        await rig.DrainAsync(TimeSpan.FromMinutes(3));
        timeline.Mark("every run final");

        var m = await rig.MeasureAsync();
        await WriteReportAsync("g1-rolling-upgrade-report.md", "G1 rolling agent upgrade: 2 orchestrator replicas, 6 → 6 agents × 3 slots", m, timeline);

        Assert.Equal(0, m.AcceptedNotSucceeded);
        Assert.Equal(0, m.LostAttempts);
        Assert.Equal(0, m.LeaseOverlaps);
        Assert.Equal(0, m.DoubleSuccesses);
    }

    private static Task<long> HostDataRunningOnAsync(G1Rig rig, string hostName) =>
        Infrastructure.HostData.ScalarAsync<long>(rig.Pg, """
            SELECT count(*) FROM task_runs t JOIN hosts h ON h.id = t.lease_host_id
            WHERE h.name = @name AND t.status IN ('leased','running')
            """, ("name", hostName));

    private static async Task WaitUntilAsync(G1Rig.Timeline timeline, TimeSpan at)
    {
        var wait = at - timeline.Elapsed;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
    }

    private async Task WriteReportAsync(string file, string title, G1Measurements m, G1Rig.Timeline timeline)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{title}");
        text.AppendLine();
        text.Append(m.Table());
        text.AppendLine();
        text.AppendLine("| At | Event |");
        text.AppendLine("| --- | --- |");
        text.Append(timeline);
        output.WriteLine(text.ToString());
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, file), text.ToString());
    }
}
