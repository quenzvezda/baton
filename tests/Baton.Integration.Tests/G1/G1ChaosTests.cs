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
    public async Task Rolling_agent_restart_under_load_fails_no_run()
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
        timeline.Mark($"{old.Count} new agents started (same build)");

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
        await WriteReportAsync("g1-rolling-restart-report.md", "G1 rolling agent restart: 2 orchestrator replicas, 6 → 6 agents × 3 slots", m, timeline);

        Assert.Equal(0, m.AcceptedNotSucceeded);
        Assert.Equal(0, m.LostAttempts);
        Assert.Equal(0, m.LeaseOverlaps);
        Assert.Equal(0, m.DoubleSuccesses);
    }

    [Fact]
    [Trait("Category", "G1")]
    public async Task Rolling_agent_upgrade_to_a_new_workflow_version_under_load_fails_no_run()
    {
        G1Executions.Reset();
        // Old build: carries g1-pipeline@1 only. New build: @1 and @2 (definitions are immutable; old versions stay).
        await using var rig = await G1Rig.StartAsync(orchestrators: 2, agents: 6, slots: 3, leaseSeconds: 30, profile: "chaos", FastRecovery,
            agentsLack: [typeof(G1PipelineV2)]);
        var timeline = new G1Rig.Timeline();
        using var stop = new CancellationTokenSource();
        timeline.Mark($"producer starts: {RunsPerSecond} runs/s of g1-pipeline@1 on 6 old agents (carry @1 only)");
        var producing = rig.ProduceAsync(RunsPerSecond, stop.Token);

        await WaitUntilAsync(timeline, TimeSpan.FromSeconds(20));
        // Runbook "Agent rollout": the Control Plane deploys first and registers the new version; the automation follows
        // latest, so every run from here on is @2 while @1 runs are still in flight.
        var (_, v2Hash) = await Infrastructure.DispatchScenario.RegisterAsync(rig.Pg, new G1PipelineV2());
        timeline.Mark("g1-pipeline@2 registered (API deploy); new runs are @2, no agent carries it yet");
        var old = rig.Agents.ToList();
        for (var i = 0; i < old.Count; i++)
            await rig.AddAgentAsync(slots: 3, preferred: i);
        timeline.Mark($"{old.Count} new agents started (carry @1 and @2)");

        // Drain the old agents right away, while they still run @1 tasks of runs started before the switch.
        foreach (var agent in old)
        {
            await WaitUntilAsync(timeline, timeline.Elapsed + TimeSpan.FromSeconds(1));
            var running = await HostDataRunningOnAsync(rig, agent.Name);
            await agent.StopAsync(TimeSpan.FromSeconds(30)); // drain: no new claims, running tasks finish and report
            timeline.Mark($"old agent {agent.Name} drained and stopped ({running} tasks were running)");
        }

        await WaitUntilAsync(timeline, timeline.Elapsed + TimeSpan.FromSeconds(30));
        await stop.CancelAsync();
        await producing;
        timeline.Mark("producer stops");
        await rig.DrainAsync(TimeSpan.FromMinutes(3));
        timeline.Mark("every run final");

        var m = await rig.MeasureAsync();
        var oldNames = old.Select(a => a.Name).ToArray();
        var versions = await VersionTableAsync(rig, oldNames);
        var v2OnOldAgents = await Infrastructure.HostData.ScalarAsync<long>(rig.Pg, """
            SELECT count(*) FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id JOIN hosts h ON h.id = a.host_id
            WHERE t.definition_hash = @hash AND h.name = ANY (@old)
            """, ("hash", v2Hash), ("old", oldNames));
        await WriteReportAsync("g1-rolling-upgrade-report.md",
            "G1 rolling agent upgrade @1 → @2: 2 orchestrator replicas, 6 → 6 agents × 3 slots", m, timeline,
            versions + $"\n@2 attempts on old agents (must be 0): {v2OnOldAgents}\n");

        Assert.Equal(0, m.AcceptedNotSucceeded);
        Assert.Equal(0, m.LostAttempts);
        Assert.Equal(0, m.LeaseOverlaps);
        Assert.Equal(0, m.DoubleSuccesses);
        Assert.Equal(0, m.HandlerOverlaps);
        Assert.Equal(0, v2OnOldAgents);
        foreach (var version in new[] { 1, 2 })
        {
            Assert.True(await Infrastructure.HostData.ScalarAsync<long>(rig.Pg, """
                SELECT count(*) FROM workflow_runs w JOIN workflow_definitions d ON d.id = w.definition_id
                WHERE w.automation_id = @a AND d.version = @v AND w.status = 'succeeded'
                """, ("a", rig.AutomationId), ("v", version)) > 0, $"No @{version} run succeeded.");
        }
    }

    /// <summary>Runs, attempts and Queued→Leased per version, and where each version's attempts ran.</summary>
    private static async Task<string> VersionTableAsync(G1Rig rig, string[] oldNames)
    {
        var text = new StringBuilder();
        text.AppendLine("| Version | Runs (succeeded / all) | Attempts on old / new agents | Queued→Leased p95 / max |");
        text.AppendLine("| --- | --- | --- | --- |");
        await using var cmd = rig.Pg.DataSource.CreateCommand("""
            SELECT d.version,
                   (SELECT count(*) FILTER (WHERE w2.status = 'succeeded') FROM workflow_runs w2 WHERE w2.definition_id = d.id AND w2.automation_id = @a),
                   (SELECT count(*) FROM workflow_runs w2 WHERE w2.definition_id = d.id AND w2.automation_id = @a),
                   count(*) FILTER (WHERE h.name = ANY (@old)), count(*) FILTER (WHERE NOT h.name = ANY (@old)),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM a.started_at - t.queued_at))::float8,
                   max(extract(epoch FROM a.started_at - t.queued_at))::float8
            FROM task_attempts a
            JOIN task_runs t ON t.id = a.task_run_id AND a.attempt = t.attempt
            JOIN workflow_runs w ON w.id = t.workflow_run_id
            JOIN workflow_definitions d ON d.id = w.definition_id
            JOIN hosts h ON h.id = a.host_id
            WHERE w.automation_id = @a
            GROUP BY d.id, d.version ORDER BY d.version
            """);
        cmd.Parameters.AddWithValue("a", rig.AutomationId);
        cmd.Parameters.AddWithValue("old", oldNames);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| @{r.GetInt32(0)} | {r.GetInt64(1)} / {r.GetInt64(2)} | {r.GetInt64(3)} / {r.GetInt64(4)} | {r.GetDouble(5) * 1000:0} / {r.GetDouble(6) * 1000:0} ms |");
        }

        return text.ToString();
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

    private async Task WriteReportAsync(string file, string title, G1Measurements m, G1Rig.Timeline timeline, string? extra = null)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{title}");
        text.AppendLine();
        text.Append(m.Table());
        if (extra is not null)
        {
            text.AppendLine();
            text.Append(extra);
        }
        text.AppendLine();
        text.AppendLine("| At | Event |");
        text.AppendLine("| --- | --- |");
        text.Append(timeline);
        output.WriteLine(text.ToString());
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, file), text.ToString());
    }
}
