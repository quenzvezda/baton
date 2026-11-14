using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Baton.Abstractions;
using Baton.Domain.Automations;
using Baton.Integration.Tests.Infrastructure;
using Baton.Orchestrator.Dispatch;
using Baton.Persistence.Queue;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Baton.Integration.Tests.G1;

/// <summary>
/// Gate G1 test rig: a dedicated, restartable Postgres, one or more orchestrator replicas with every loop, real
/// in-process agents that fail over between replicas, one host group and two automations of
/// <see cref="G1Pipeline"/> — a manual one the producer fires at a fixed rate and a cron one firing every minute.
/// </summary>
internal sealed class G1Rig : IAsyncDisposable
{
    private readonly List<OrchestratorHost?> _orchestrators;
    private readonly List<AgentProcess> _agents = [];
    private readonly Action<DispatchOptions>? _dispatch;

    private G1Rig(PostgresFixture pg, List<OrchestratorHost?> orchestrators, Action<DispatchOptions>? dispatch,
        Guid groupId, string group, string token, Guid automationId, Guid cronAutomationId)
    {
        Pg = pg;
        _orchestrators = orchestrators;
        _dispatch = dispatch;
        GroupId = groupId;
        Group = group;
        Token = token;
        AutomationId = automationId;
        CronAutomationId = cronAutomationId;
        Creator = new RunCreator(pg.DataSource);
    }

    public PostgresFixture Pg { get; }
    public Guid GroupId { get; }
    public string Group { get; }
    public string Token { get; }
    public Guid AutomationId { get; }
    public Guid CronAutomationId { get; }
    public RunCreator Creator { get; }
    public IReadOnlyList<AgentProcess> Agents => _agents;

    /// <summary>Runs the producer accepted (<see cref="FireOutcome.Created"/>): none of them may be lost.</summary>
    public ConcurrentBag<Guid> Accepted { get; } = [];

    /// <summary>Fire attempts that failed (e.g. while Postgres was down): never accepted, so not "lost".</summary>
    public ConcurrentQueue<string> RefusedFires { get; } = new();

    /// <param name="agentsLack">Definition types the initial agents do not carry (an older build).</param>
    public static async Task<G1Rig> StartAsync(
        int orchestrators, int agents, int slots, int leaseSeconds, string profile, Action<DispatchOptions>? dispatch = null,
        Type[]? agentsLack = null)
    {
        var pg = PostgresFixture.Restartable();
        await pg.InitializeAsync();

        await DispatchScenario.RegisterAsync(pg, new G1Pipeline());
        var (groupId, group) = await HostData.CreateGroupAsync(pg, "g1");
        await HostData.ExecuteAsync(pg, "UPDATE host_groups SET lease_seconds = @s WHERE id = @id", ("s", leaseSeconds), ("id", groupId));
        var (_, token) = await HostData.IssueCredentialAsync(pg, groupId);
        var automationId = await RunScenario.InsertAutomationAsync(pg, groupId, "g1-pipeline", policy: "allow", maxRuns: 100_000);
        var cronAutomationId = await RunScenario.InsertAutomationAsync(pg, groupId, "g1-pipeline", policy: "allow", maxRuns: 100);
        await HostData.ExecuteAsync(pg, "UPDATE automations SET parameters = jsonb_build_object('profile', @p::text) WHERE id = ANY (@ids)",
            ("p", profile), ("ids", new[] { automationId, cronAutomationId }));

        var hosts = new List<OrchestratorHost?>();
        for (var i = 0; i < orchestrators; i++)
            hosts.Add(await OrchestratorHost.StartAsync(pg, dispatch, loops: true));

        var rig = new G1Rig(pg, hosts, dispatch, groupId, group, token, automationId, cronAutomationId);
        for (var i = 0; i < agents; i++)
            await rig.AddAgentAsync(slots, preferred: i % orchestrators, lacks: agentsLack);
        await Eventually.UntilAsync(() => HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM hosts WHERE host_group_id = @g", ("g", groupId)),
            n => n == agents, "every agent registered", TimeSpan.FromSeconds(60));
        return rig;
    }

    /// <summary>
    /// A new agent that prefers replica <paramref name="preferred"/> and fails over to the others. It carries every
    /// public definition of this assembly except <paramref name="lacks"/>, so it reports only those hashes.
    /// </summary>
    public async Task<AgentProcess> AddAgentAsync(int slots, int preferred = 0, Type[]? lacks = null)
    {
        var live = _orchestrators.Where(o => o is not null).Select(o => o!.Address).ToList();
        var first = live[preferred % live.Count];
        var agent = await AgentProcess.StartAsync(first, Group, Token, slots, failover: live.Where(a => a != first).ToList(),
            services: lacks is { Length: > 0 } ? s => WithoutDefinitions(s, lacks) : null);
        _agents.Add(agent);
        return agent;
    }

    private static void WithoutDefinitions(IServiceCollection services, Type[] lacks)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IWorkflowDefinition) && lacks.Contains(services[i].ImplementationType))
                services.RemoveAt(i);
        }
    }

    /// <summary>Starts the cron trigger (<c>* * * * *</c>, UTC) from the next minute.</summary>
    public Task StartCronAsync() => HostData.ExecuteAsync(Pg, """
        INSERT INTO automation_triggers (id, automation_id, kind, cron_expr, time_zone, next_fire_at)
        VALUES (gen_random_uuid(), @a, 'cron', '* * * * *', 'UTC', date_trunc('minute', now()) + interval '1 minute')
        """, ("a", CronAutomationId));

    public Task StopCronAsync() =>
        HostData.ExecuteAsync(Pg, "UPDATE automation_triggers SET enabled = false WHERE automation_id = @a", ("a", CronAutomationId));

    /// <summary>Stops replica <paramref name="index"/> as a crashed process would look to everyone else.</summary>
    public async Task KillOrchestratorAsync(int index)
    {
        var host = _orchestrators[index] ?? throw new InvalidOperationException($"Replica {index} is already down.");
        _orchestrators[index] = null;
        await host.DisposeAsync();
    }

    /// <summary>Fires the manual automation <paramref name="perSecond"/> times a second until <paramref name="ct"/>.</summary>
    public async Task ProduceAsync(double perSecond, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / perSecond));
        var inFlight = new List<Task>();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                inFlight.Add(FireOnceAsync());
                inFlight.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        await Task.WhenAll(inFlight);
    }

    private async Task FireOnceAsync()
    {
        try
        {
            var result = await Creator.FireAsync(
                new FireRequest(AutomationId, null, AutomationRules.ManualKind, Guid.NewGuid().ToString()), CancellationToken.None);
            if (result.Outcome == FireOutcome.Created)
                Accepted.Add(result.RunId!.Value);
            else
                RefusedFires.Enqueue(result.Outcome.ToString());
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            RefusedFires.Enqueue(ex.GetType().Name);
        }
    }

    /// <summary>Waits until every run of both automations is final.</summary>
    public Task DrainAsync(TimeSpan within) =>
        Eventually.UntilAsync(() => HostData.ScalarAsync<long>(Pg, """
                SELECT count(*) FROM workflow_runs WHERE automation_id = ANY (@ids) AND status NOT IN ('succeeded','failed','cancelled','timed_out')
                """, ("ids", new[] { AutomationId, CronAutomationId })),
            n => n == 0, "every run to finish", within);

    public async Task<G1Measurements> MeasureAsync()
    {
        var ids = new[] { AutomationId, CronAutomationId };
        await using var conn = await Pg.DataSource.OpenConnectionAsync();

        var outcomes = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            "SELECT status, count(*) FROM workflow_runs WHERE automation_id = ANY (@ids) GROUP BY status", conn))
        {
            cmd.Parameters.AddWithValue("ids", ids);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                outcomes[r.GetString(0)] = r.GetInt64(1);
        }

        // Queued -> Leased: the attempt's lease time against the task's latest queue time (same attempt).
        var latency = await RowAsync(conn, """
            SELECT count(*)::float8,
                   percentile_cont(0.5)  WITHIN GROUP (ORDER BY s), percentile_cont(0.95) WITHIN GROUP (ORDER BY s),
                   percentile_cont(0.99) WITHIN GROUP (ORDER BY s), max(s)
            FROM (SELECT extract(epoch FROM a.started_at - t.queued_at)::float8 AS s
                  FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id AND a.attempt = t.attempt
                  JOIN workflow_runs w ON w.id = t.workflow_run_id
                  WHERE w.automation_id = ANY (@ids)) x
            """, ids);

        // Dispatches per second over the busy window (seconds with at least one lease).
        var rate = await RowAsync(conn, """
            SELECT count(*)::float8, avg(n), max(n),
                   (SELECT count(*) FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id
                    JOIN workflow_runs w ON w.id = t.workflow_run_id WHERE w.automation_id = ANY (@ids))::float8
            FROM (SELECT date_trunc('second', a.started_at) AS sec, count(*)::float8 AS n
                  FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id JOIN workflow_runs w ON w.id = t.workflow_run_id
                  WHERE w.automation_id = ANY (@ids) GROUP BY 1) x
            """, ids);

        // Cron: run creation against the fire time carried in the idempotency key.
        var cron = await RowAsync(conn, """
            SELECT count(*)::float8, COALESCE(max(s), 0), COALESCE(avg(s), 0)
            FROM (SELECT extract(epoch FROM w.created_at - (substring(w.idempotency_key FROM ':(\d{4}-\d{2}-\d{2}T[^:]+:[^:]+:[^:]+)$'))::timestamptz)::float8 AS s
                  FROM workflow_runs w WHERE w.automation_id = @cron) x
            """, ids, CronAutomationId);

        // Fencing: no attempt leased before the previous attempt of the same task was closed.
        var overlaps = await ScalarAsync(conn, """
            SELECT count(*)::float8 FROM task_attempts a
            JOIN task_attempts b ON b.task_run_id = a.task_run_id AND b.attempt > a.attempt
            JOIN task_runs t ON t.id = a.task_run_id JOIN workflow_runs w ON w.id = t.workflow_run_id
            WHERE w.automation_id = ANY (@ids) AND b.started_at < COALESCE(a.finished_at, 'infinity')
            """, ids);
        var doubleSuccess = await ScalarAsync(conn, """
            SELECT count(*)::float8 FROM (
              SELECT a.task_run_id FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id
              JOIN workflow_runs w ON w.id = t.workflow_run_id
              WHERE w.automation_id = ANY (@ids) AND a.outcome = 'succeeded' GROUP BY a.task_run_id HAVING count(*) > 1) x
            """, ids);
        var lostAttempts = await ScalarAsync(conn, """
            SELECT count(*)::float8 FROM task_attempts a JOIN task_runs t ON t.id = a.task_run_id
            JOIN workflow_runs w ON w.id = t.workflow_run_id WHERE w.automation_id = ANY (@ids) AND a.outcome = 'lost'
            """, ids);

        var accepted = Accepted.ToArray();
        long missing;
        await using (var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM unnest(@accepted::uuid[]) AS a(id)
            WHERE NOT EXISTS (SELECT 1 FROM workflow_runs w WHERE w.id = a.id AND w.status = 'succeeded')
            """, conn))
        {
            cmd.Parameters.AddWithValue("accepted", accepted);
            missing = (long)(await cmd.ExecuteScalarAsync())!;
        }

        return new G1Measurements(outcomes, accepted.Length, missing, RefusedFires.Count,
            (long)latency[0], latency[1], latency[2], latency[3], latency[4],
            (long)rate[3], (long)rate[0], rate[1], rate[2],
            (long)cron[0], cron[1], cron[2],
            (long)overlaps, (long)doubleSuccess, (long)lostAttempts, G1Executions.Executions, G1Executions.Overlaps);
    }

    private static async Task<double[]> RowAsync(NpgsqlConnection conn, string sql, Guid[] ids, Guid? cron = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ids", ids);
        if (cron is { } c)
            cmd.Parameters.AddWithValue("cron", c);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? 0 : r.GetDouble(i)).ToArray();
    }

    private static async Task<double> ScalarAsync(NpgsqlConnection conn, string sql, Guid[] ids) => (await RowAsync(conn, sql, ids))[0];

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agents)
            await agent.DisposeAsync();
        foreach (var host in _orchestrators)
        {
            if (host is not null)
                await host.DisposeAsync();
        }

        await Pg.DisposeAsync();
    }

    /// <summary>Stopwatch-stamped log of what the test did, for the report.</summary>
    public sealed class Timeline
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly StringBuilder _text = new();

        public TimeSpan Elapsed => _clock.Elapsed;

        public void Mark(string what) =>
            _text.AppendLine(CultureInfo.InvariantCulture, $"| {_clock.Elapsed:mm\\:ss} | {what} |");

        public override string ToString() => _text.ToString();
    }
}

internal sealed record G1Measurements(
    IReadOnlyDictionary<string, long> Outcomes, long Accepted, long AcceptedNotSucceeded, long RefusedFires,
    long Attempts, double LatencyP50, double LatencyP95, double LatencyP99, double LatencyMax,
    long Dispatches, long BusySeconds, double RateAvg, double RatePeak,
    long CronRuns, double CronMaxLateness, double CronAvgLateness,
    long LeaseOverlaps, long DoubleSuccesses, long LostAttempts, long HandlerExecutions, long HandlerOverlaps)
{
    public string Table()
    {
        var c = CultureInfo.InvariantCulture;
        var t = new StringBuilder();
        t.AppendLine("| Metric | Value |");
        t.AppendLine("| --- | --- |");
        t.AppendLine(c, $"| Runs by status | {string.Join(", ", Outcomes.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key} {o.Value}"))} |");
        t.AppendLine(c, $"| Runs accepted by the producer / not Succeeded | {Accepted} / {AcceptedNotSucceeded} |");
        t.AppendLine(c, $"| Fires refused (never accepted) | {RefusedFires} |");
        t.AppendLine(c, $"| Task attempts (dispatches) | {Dispatches} |");
        t.AppendLine(c, $"| Dispatch rate, busy seconds avg / peak | {RateAvg:0.0} /s / {RatePeak:0} /s over {BusySeconds} s |");
        t.AppendLine(c, $"| Queued→Leased p50 / p95 / p99 / max | {LatencyP50 * 1000:0} / {LatencyP95 * 1000:0} / {LatencyP99 * 1000:0} / {LatencyMax * 1000:0} ms |");
        t.AppendLine(c, $"| Cron runs; lateness avg / max | {CronRuns}; {CronAvgLateness * 1000:0} / {CronMaxLateness * 1000:0} ms |");
        t.AppendLine(c, $"| Lost attempts (reaped leases) | {LostAttempts} |");
        t.AppendLine(c, $"| Overlapping leases of one task (DB) | {LeaseOverlaps} |");
        t.AppendLine(c, $"| Tasks with more than one accepted success | {DoubleSuccesses} |");
        t.AppendLine(c, $"| Handler executions / overlapping executions of one task | {HandlerExecutions} / {HandlerOverlaps} |");
        return t.ToString();
    }
}
