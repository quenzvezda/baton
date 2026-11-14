using System.Diagnostics;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Runs;
using Xunit.Abstractions;

namespace Baton.Integration.Tests.G1;

/// <summary>
/// M7 task 4.1: the queue statistics query over data the size of the G1 profile (docs/reports/m7-queue-stats.md).
/// By hand: <c>dotnet test tests/Baton.Integration.Tests -c Release --filter FullyQualifiedName~M7QueueStatsLoadTests</c>.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class M7QueueStatsLoadTests(PostgresFixture pg, ITestOutputHelper output)
{
    private const int Groups = 4;
    private const int FinishedRuns = 4_500;     // one G1 run: 4,500 runs x 4 tasks = 18,000 attempts
    private const int HistoryAttempts = 300_000; // about a month of the 10k/day profile, finished hours ago
    private const int Backlog = 5_000;          // the M6 backlog, queued in group 0
    private const int Running = 24;             // 6 agents x 4 slots
    private const int LostLastHour = 120;

    [Fact]
    [Trait("Category", "Load")]
    public async Task Queue_stats_stay_fast_over_g1_sized_data()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var groups = new List<Guid> { s.GroupId };
        for (var i = 1; i < Groups; i++)
            groups.Add((await HostData.CreateGroupAsync(pg)).Id);

        var seed = Stopwatch.StartNew();
        await SeedAsync(s, host, groups);
        output.WriteLine($"Seeded in {seed.Elapsed.TotalSeconds:0.0} s");
        await HostData.ExecuteAsync(pg, "ANALYZE");

        var stats = new QueueStats(pg.DataSource);
        await stats.ReadAsync(CancellationToken.None);
        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            await stats.ReadAsync(CancellationToken.None);
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var result = (await stats.ReadAsync(CancellationToken.None)).Where(g => groups.Contains(g.HostGroupId)).ToList();
        var busy = result.Single(g => g.HostGroupId == s.GroupId);
        Assert.Equal(Backlog, busy.Queued);
        Assert.Equal(Running, busy.ActiveLeases);
        Assert.Equal(LostLastHour, busy.LeaseExpirationsLastHour);

        timings.Sort();
        output.WriteLine($"ReadAsync over 20 calls: p50 {timings[9]:0.0} ms, p95 {timings[18]:0.0} ms, max {timings[^1]:0.0} ms");

        await using var cmd = pg.DataSource.CreateCommand("EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) " + QueueStats.Sql);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            output.WriteLine(reader.GetString(0));

        Assert.True(timings[18] < 100, $"p95 {timings[18]:0.0} ms");
    }

    private async Task SeedAsync(DispatchScenario s, Guid host, List<Guid> groups)
    {
        var parameters = new (string, object)[]
        {
            ("def", s.DefinitionId), ("hash", s.Hash), ("host", host), ("groups", groups.ToArray()), ("busy", s.GroupId),
            ("runs", FinishedRuns), ("history", HistoryAttempts), ("backlog", Backlog), ("running", Running), ("lost", LostLastHour),
        };

        await using var connection = await pg.DataSource.OpenConnectionAsync();

        // Finished runs spread over the groups, 4 succeeded tasks each with one attempt, over the last day.
        await ExecuteAsync(connection, """
            CREATE TEMP TABLE seed_runs AS
            SELECT gen_random_uuid() AS id, (@groups::uuid[])[1 + i % cardinality(@groups::uuid[])] AS grp,
                   now() - make_interval(secs => i * 19) AS at, i
            FROM generate_series(1, @runs) i;
            INSERT INTO workflow_runs (id, definition_id, status, priority, host_group_id, parameters, trigger_kind,
                                       idempotency_key, created_at, started_at, finished_at)
            SELECT id, @def, 'succeeded', 50, grp, '{}', 'manual', 'm7-load-' || id, at, at, at + interval '2 seconds' FROM seed_runs;
            CREATE TEMP TABLE seed_tasks AS
            SELECT gen_random_uuid() AS id, r.id AS run, r.grp, r.at, k AS key
            FROM seed_runs r CROSS JOIN unnest(ARRAY['fetch','left','right','merge']) k;
            INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, attempt, max_attempts,
                                   timeout, priority, effective_priority, host_group_id, definition_hash, started_at, finished_at)
            SELECT id, run, key, 'succeeded', 'all_success', 0, 1, 3, interval '1 hour', 50, 50, grp, @hash, at, at + interval '1 second'
            FROM seed_tasks;
            INSERT INTO task_attempts (task_run_id, attempt, host_id, lease_token, started_at, finished_at, outcome)
            SELECT id, 1, @host, nextval('lease_token_seq'), at, at + interval '1 second', 'succeeded' FROM seed_tasks;
            """, parameters);

        // Older attempts of the same tasks (retries), finished hours to days ago this month, a few of them lost long ago.
        await ExecuteAsync(connection, """
            INSERT INTO task_attempts (task_run_id, attempt, host_id, lease_token, started_at, finished_at, outcome)
            SELECT t.id, 100 + g.i, @host, g.i, now() - make_interval(hours => 2 + g.i % h.span), now() - make_interval(hours => 2 + g.i % h.span),
                   CASE WHEN g.i % 50 = 0 THEN 'lost' ELSE 'failed' END
            FROM generate_series(1, @history) g(i)
            -- Stay inside the current month: only its partition (and later ones) exist.
            CROSS JOIN (SELECT greatest(1, (extract(epoch FROM now() - date_trunc('month', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC') / 3600)::int - 3) AS span) h
            JOIN LATERAL (SELECT id FROM seed_tasks OFFSET g.i % 18000 LIMIT 1) t ON true;
            """, parameters);

        // The busy group: a queued backlog, running tasks and lease expirations in the last hour.
        await ExecuteAsync(connection, """
            CREATE TEMP TABLE seed_live AS
            SELECT gen_random_uuid() AS id, i, CASE WHEN i <= @running THEN 'running' ELSE 'queued' END AS status
            FROM generate_series(1, @backlog + @running) i;
            INSERT INTO workflow_runs (id, definition_id, status, priority, host_group_id, parameters, trigger_kind, idempotency_key, started_at)
            VALUES ('00000000-0000-7000-8000-00000000c0de', @def, 'running', 50, @busy, '{}', 'manual', 'm7-load-live-' || gen_random_uuid(), now());
            INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, attempt, max_attempts,
                                   timeout, priority, effective_priority, host_group_id, definition_hash, queued_at,
                                   lease_host_id, lease_token, lease_expires_at)
            SELECT id, '00000000-0000-7000-8000-00000000c0de', 'live-' || i, status, 'all_success', 0, 1, 3, interval '1 hour', 10, 10,
                   @busy, @hash, now() - make_interval(secs => i),
                   CASE WHEN status = 'running' THEN @host END, CASE WHEN status = 'running' THEN i END,
                   CASE WHEN status = 'running' THEN now() + interval '1 minute' END
            FROM seed_live;
            INSERT INTO task_attempts (task_run_id, attempt, host_id, lease_token, started_at, finished_at, outcome)
            SELECT l.id, 50 + l.i, @host, l.i, now() - make_interval(mins => l.i % 55 + 2), now() - make_interval(mins => l.i % 55 + 1), 'lost'
            FROM seed_live l WHERE l.i > @running AND l.i <= @running + @lost;
            """, parameters);
    }

    private static async Task ExecuteAsync(Npgsql.NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        await using var cmd = new Npgsql.NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            if (sql.Contains("@" + name, StringComparison.Ordinal))
                cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }
}
