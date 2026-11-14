using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using Baton.ControlPlane.Api.Security;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Loops;
using Baton.Persistence.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Baton.Integration.Tests.Runs;

/// <summary>OBS-04 queue statistics and the queue and lease metrics (M7 group 4).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class QueueStatsTests(PostgresFixture pg)
{
    [Fact]
    public async Task Queue_stats_show_queued_oldest_wait_and_active_leases()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync(slots: 3);
        for (var i = 0; i < 15; i++)
            await s.StartRunAsync();
        Assert.Equal(3, (await s.ClaimAsync(host, 3)).Tasks.Count);
        await HostData.ExecuteAsync(pg, """
            UPDATE task_runs SET queued_at = now() - interval '7 minutes'
            WHERE id = (SELECT id FROM task_runs WHERE host_group_id = @group AND status = 'queued' ORDER BY id LIMIT 1)
            """, ("group", s.GroupId));
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var viewer = api.Client(BatonRoles.Viewer);

        var stats = (await viewer.GetFromJsonAsync<QueueGroupStats[]>("/api/queue/stats"))!.Single(g => g.HostGroupId == s.GroupId);

        Assert.Equal(12, stats.Queued);
        Assert.Equal(3, stats.ActiveLeases);
        Assert.InRange(stats.OldestWaitSeconds!.Value, 7 * 60, 7 * 60 + 30);
        Assert.Equal(s.GroupName, stats.Name);
        Assert.False(stats.Paused);
    }

    [Fact]
    public async Task Lease_expirations_count_the_last_hour_only()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var task = await s.TaskAsync(runId, "a");
        foreach (var (attempt, minutesAgo) in new[] { (1, 10), (2, 50), (3, 70) })
        {
            await HostData.ExecuteAsync(pg, """
                INSERT INTO task_attempts (task_run_id, attempt, host_id, lease_token, started_at, finished_at, outcome)
                VALUES (@task, @attempt, @host, @attempt, now() - make_interval(mins => @ago + 1), now() - make_interval(mins => @ago), 'lost')
                """, ("task", task.Id), ("attempt", attempt), ("host", host), ("ago", minutesAgo));
        }

        var stats = (await new QueueStats(pg.DataSource).ReadAsync(CancellationToken.None)).Single(g => g.HostGroupId == s.GroupId);

        Assert.Equal(2, stats.LeaseExpirationsLastHour);
    }

    [Fact]
    public async Task Idle_group_is_listed_with_zeros()
    {
        var (groupId, name) = await HostData.CreateGroupAsync(pg);

        var stats = (await new QueueStats(pg.DataSource).ReadAsync(CancellationToken.None)).Single(g => g.HostGroupId == groupId);

        Assert.Equal(new QueueGroupStats(groupId, name, false, 0, null, 0, 0, 0), stats);
    }

    [Fact]
    public async Task Expired_lease_increments_the_group_counter()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await s.ExpireLeaseAsync(lease.TaskRunId);
        var metrics = TestMetrics.Create(out var factory);
        var expired = new List<(long Value, string? Group)>();
        using var listener = Listen<long>(factory, "baton.leases.expired", (value, group) => expired.Add((value, group)));
        var loop = new ReaperLoop(s.Reaper, Options.Create(new DispatchOptions()), metrics, NullLogger<ReaperLoop>.Instance);

        await loop.TickAsync(CancellationToken.None);

        Assert.Contains((1L, s.GroupId.ToString()), expired);
    }

    [Fact]
    public async Task Oldest_wait_gauge_reports_within_30_s()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var runId = await s.StartRunAsync();
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET queued_at = now() - interval '7 minutes' WHERE workflow_run_id = @id", ("id", runId));
        var metrics = TestMetrics.Create(out var factory);
        var loop = new QueueMetricsLoop(new QueueStats(pg.DataSource), metrics, NullLogger<QueueMetricsLoop>.Instance);
        Assert.True(QueueMetricsLoop.RefreshInterval <= TimeSpan.FromSeconds(30));

        await loop.TickAsync(CancellationToken.None);

        var waits = new Dictionary<string, double>();
        var depths = new Dictionary<string, double>();
        using var waitListener = Listen<double>(factory, "baton.queue.oldest_wait", (value, group) => waits[group!] = value);
        using var depthListener = Listen<double>(factory, "baton.queue.depth", (value, group) => depths[group!] = value);
        waitListener.RecordObservableInstruments();
        depthListener.RecordObservableInstruments();

        Assert.InRange(waits[s.GroupId.ToString()], 420, 450);
        Assert.Equal(1, depths[s.GroupId.ToString()]);
    }

    private static MeterListener Listen<T>(IMeterFactory factory, string name, Action<T, string?> record)
        where T : struct
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Scope == factory && instrument.Name == name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((_, value, tags, _) =>
        {
            string? group = null;
            foreach (var tag in tags)
            {
                if (tag.Key == OrchestratorMetrics.HostGroupTag)
                    group = tag.Value as string;
            }

            record(value, group);
        });
        listener.Start();
        return listener;
    }
}
