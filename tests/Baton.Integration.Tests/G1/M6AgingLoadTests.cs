using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace Baton.Integration.Tests.G1;

/// <summary>
/// M6 exit criterion "Aging keeps the dispatch index usable (only changed rows updated) — measured under G1 load
/// profile" (task 3.4). The G1 load runs (20 dispatches/s, two replicas, six agents) next to a backlog of queued
/// tasks that no agent may take (they need a label no host has), so they wait and age. The aging step is cut to
/// 30 s so the backlog reaches the cap of 20 within 10 minutes: every backlog task is written exactly 20 times,
/// and the G1 tasks, which wait milliseconds, never. Runs by hand (<c>--filter FullyQualifiedName~M6AgingLoadTests</c>);
/// <c>BATON_G1_LOAD_MINUTES</c> shortens it.
/// </summary>
public sealed class M6AgingLoadTests(ITestOutputHelper output)
{
    private const int Backlog = 5_000;
    private const int Cap = 20;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    [Fact]
    [Trait("Category", "G1")]
    public async Task Aging_a_backlog_under_the_g1_load_writes_only_changed_rows_and_keeps_dispatch_fast()
    {
        var minutes = double.Parse(Environment.GetEnvironmentVariable("BATON_G1_LOAD_MINUTES") ?? "15", CultureInfo.InvariantCulture);
        var ticks = new ConcurrentQueue<(DateTimeOffset At, long Aged)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "baton.tasks.aged")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => ticks.Enqueue((DateTimeOffset.UtcNow, value)));
        listener.Start();

        G1Executions.Reset();
        await using var rig = await G1Rig.StartAsync(2, 6, 4, leaseSeconds: 60, profile: "load", dispatch: o => o.AgingStep = Step);
        await InsertBacklogAsync(rig);
        var indexBefore = await IndexBytesAsync(rig);
        var timeline = new G1Rig.Timeline();

        await rig.StartCronAsync();
        timeline.Mark($"producer starts with {Backlog} backlog tasks queued; aging step {Step.TotalSeconds:0} s, cap {Cap}");
        using (var stop = new CancellationTokenSource(TimeSpan.FromMinutes(minutes)))
            await rig.ProduceAsync(5, stop.Token);
        timeline.Mark("producer stops");
        await rig.StopCronAsync();
        await rig.DrainAsync(TimeSpan.FromMinutes(2));
        timeline.Mark("every run final");

        var m = await rig.MeasureAsync();
        var indexAfter = await IndexBytesAsync(rig);
        var backlogAt = await BacklogPrioritiesAsync(rig);
        var perTick = ticks.ToArray();
        var report = Report(minutes, m, perTick, indexBefore, indexAfter, backlogAt, timeline);
        output.WriteLine(report);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "m6-aging-load-report.md"), report);

        Assert.Equal(0, m.AcceptedNotSucceeded);
        Assert.True(m.LatencyP95 < 2, $"p95 Queued→Leased {m.LatencyP95:0.000} s.");
        Assert.Single(backlogAt); // the backlog ages in step
        if (minutes * 60 >= (Cap + 1) * Step.TotalSeconds)
        {
            Assert.Equal(10 + Cap, backlogAt[0]);
            Assert.Equal((long)Backlog * Cap, perTick.Sum(t => t.Aged)); // each backlog task once per step, nothing else
        }
    }

    private static Task InsertBacklogAsync(G1Rig rig) => Infrastructure.HostData.ExecuteAsync(rig.Pg, """
        WITH def AS (SELECT id, graph_hash FROM workflow_definitions WHERE key = 'g1-pipeline' ORDER BY version DESC LIMIT 1),
        runs AS (
          INSERT INTO workflow_runs (id, definition_id, status, priority, host_group_id, parameters, trigger_kind, idempotency_key, started_at)
          SELECT gen_random_uuid(), def.id, 'running', 10, @g, '{}', 'manual', 'm6-backlog-' || n, now()
          FROM def, generate_series(1, @n) n
          RETURNING id)
        INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, max_attempts, timeout,
                               priority, effective_priority, host_group_id, required_labels, definition_hash, queued_at)
        SELECT gen_random_uuid(), runs.id, 'fetch', 'queued', 'all_success', 0, 1, interval '1 hour',
               10, 10, @g, '{m6-no-host-has-this}', def.graph_hash, now()
        FROM runs, def
        """, ("g", rig.GroupId), ("n", Backlog));

    private static Task<long> IndexBytesAsync(G1Rig rig) =>
        Infrastructure.HostData.ScalarAsync<long>(rig.Pg, "SELECT pg_relation_size('ix_tasks_dispatch')");

    private static async Task<short[]> BacklogPrioritiesAsync(G1Rig rig)
    {
        var values = new List<short>();
        await using var cmd = rig.Pg.DataSource.CreateCommand(
            "SELECT DISTINCT effective_priority FROM task_runs WHERE required_labels = '{m6-no-host-has-this}' ORDER BY 1");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetInt16(0));
        return [.. values];
    }

    private static string Report(double minutes, G1Measurements m, (DateTimeOffset At, long Aged)[] ticks, long indexBefore,
        long indexAfter, short[] backlogAt, G1Rig.Timeline timeline)
    {
        var c = CultureInfo.InvariantCulture;
        var writing = ticks.Where(t => t.Aged > 0).ToArray();
        var text = new StringBuilder();
        text.AppendLine(c, $"M6 aging under the G1 load: 2 replicas, 6 agents × 4 slots, 20 dispatches/s for {minutes:0.#} min, {Backlog} backlog tasks");
        text.AppendLine();
        text.Append(m.Table());
        text.AppendLine();
        text.AppendLine("| Aging | Value |");
        text.AppendLine("| --- | --- |");
        text.AppendLine(c, $"| Ticks that wrote rows | {writing.Length} |");
        text.AppendLine(c, $"| Rows written in all / per writing tick (min / max) | {ticks.Sum(t => t.Aged)} / {(writing.Length > 0 ? writing.Min(t => t.Aged) : 0)} / {(writing.Length > 0 ? writing.Max(t => t.Aged) : 0)} |");
        text.AppendLine(c, $"| Expected writes (backlog × cap) | {(long)Backlog * Cap} |");
        text.AppendLine(c, $"| Backlog effective priority at the end | {string.Join(", ", backlogAt)} |");
        text.AppendLine(c, $"| `ix_tasks_dispatch` size before / after | {indexBefore / 1024} / {indexAfter / 1024} KiB |");
        text.AppendLine();
        text.AppendLine("| At | Event |");
        text.AppendLine("| --- | --- |");
        text.Append(timeline);
        return text.ToString();
    }
}
