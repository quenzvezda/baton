using System.Diagnostics.Metrics;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Health;
using Baton.Orchestrator.Loops;
using Baton.Persistence.History;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Baton.Integration.Tests.Schema;

/// <summary>add-history-retention: 90-day retention (ADR-0011), partition health and limit-correction signals.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class HistoryRetentionTests(PostgresFixture pg)
{
    [Fact]
    public async Task Partitions_whose_month_ended_over_90_days_ago_are_dropped()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync(); // DDL rolls back on dispose
        var expired = await CreateMonthPartitionAsync(conn, tx, "task_logs", monthsBack: 5);
        var expiredEvents = await CreateMonthPartitionAsync(conn, tx, "run_events", monthsBack: 4);

        var dropped = await HistoryRetention.DropExpiredPartitionsAsync(conn, tx, CancellationToken.None);

        Assert.Equal([expiredEvents, expired], dropped.Order(StringComparer.Ordinal));
        Assert.False(await ExistsAsync(conn, tx, expired));
        Assert.False(await ExistsAsync(conn, tx, expiredEvents));
    }

    [Fact]
    public async Task Partition_still_inside_the_window_is_kept()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Last month ended at most 31 days ago: some rows may be older, but the month is not past 90 days.
        var lastMonth = await CreateMonthPartitionAsync(conn, tx, "task_attempts", monthsBack: 1);
        await Execute(conn, tx, """
            CREATE TABLE task_attempts_archive PARTITION OF task_attempts
              FOR VALUES FROM ('2001-01-01 00:00Z') TO ('2001-02-01 00:00Z')
            """);

        var dropped = await HistoryRetention.DropExpiredPartitionsAsync(conn, tx, CancellationToken.None);

        Assert.Empty(dropped);
        Assert.True(await ExistsAsync(conn, tx, lastMonth));
        Assert.True(await ExistsAsync(conn, tx, "task_attempts_archive")); // not Baton's naming: never dropped
    }

    [Fact]
    public async Task Finished_runs_older_than_90_days_are_purged_with_their_tasks()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "queue", maxRuns: 10);
        var old = await s.StartRunAsync();
        var recent = await s.StartRunAsync();
        await FinishAsync(old, daysAgo: 91);
        await FinishAsync(recent, daysAgo: 89);

        var purged = await PurgeAsync();

        Assert.True(purged >= 1);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM workflow_runs WHERE id = @id", old));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM task_runs WHERE workflow_run_id = @id", old));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM workflow_runs WHERE id = @id", recent));
        Assert.True(await CountAsync("SELECT count(*) FROM task_runs WHERE workflow_run_id = @id", recent) > 0);
    }

    [Fact]
    public async Task Active_runs_are_never_purged()
    {
        var s = await RunScenario.CreateAsync(pg);
        var running = await s.StartRunAsync();
        await HostData.ExecuteAsync(pg, """
            UPDATE workflow_runs SET created_at = now() - interval '100 days', started_at = now() - interval '100 days'
            WHERE id = @id
            """, ("id", running));

        await PurgeAsync();

        Assert.Equal("running", await s.RunStatusAsync(running));
        Assert.True(await CountAsync("SELECT count(*) FROM task_runs WHERE workflow_run_id = @id", running) > 0);
    }

    [Fact]
    public async Task Maintenance_tick_drops_an_expired_partition()
    {
        string expired;
        await using (var conn = await pg.DataSource.OpenConnectionAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            expired = await CreateMonthPartitionAsync(conn, tx, "task_logs", monthsBack: 6);
            await tx.CommitAsync();
        }

        var loop = new PartitionMaintenanceLoop(pg.DataSource, TestMetrics.Create(), NullLogger<PartitionMaintenanceLoop>.Instance);
        Assert.True(await loop.RunOnceAsync(CancellationToken.None));

        Assert.True(await HostData.ScalarAsync<bool>(pg, "SELECT to_regclass(@name) IS NULL", ("name", expired)));
    }

    [Fact]
    public async Task Health_is_degraded_when_future_partitions_run_low()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Leave task_logs with next month only: the months 2 and 3 ahead are dropped (rolled back on dispose).
        foreach (var monthsAhead in new[] { 2, 3 })
        {
            var name = await HostData.ScalarAsync<string>(pg,
                "SELECT 'task_logs' || to_char(date_trunc('month', now() AT TIME ZONE 'UTC') + make_interval(months => @m), '\"_y\"YYYY\"m\"MM')",
                ("m", monthsAhead));
            await Execute(conn, tx, $"DROP TABLE \"{name}\"");
        }

        var shortTables = await HistoryRetention.FindTablesShortOfFuturePartitionsAsync(
            conn, tx, HistoryPartitionsHealthCheck.MinFutureMonths, CancellationToken.None);

        Assert.Equal(["task_logs"], shortTables);
    }

    [Fact]
    public async Task Health_is_healthy_with_partitions_ahead()
    {
        var check = new HistoryPartitionsHealthCheck(pg.DataSource);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Limit_correction_is_counted_by_kind()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), maxConcurrentTasks: 10);
        await HostData.ExecuteAsync(pg, "UPDATE concurrency_limits SET in_use = 3 WHERE key = @key", ("key", DbNames.GroupTasksLimit(s.GroupId)));
        var metrics = TestMetrics.Create(out var factory);
        var kinds = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Scope == factory && instrument.Name == "baton.limits.corrections")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "limit.kind")
                    kinds.AddRange(Enumerable.Repeat(tag.Value as string, (int)value));
            }
        });
        listener.Start();

        var loop = new LimitReconcilerLoop(pg.DataSource, Options.Create(new DispatchOptions()), metrics,
            NullLogger<LimitReconcilerLoop>.Instance);
        Assert.True(await loop.RunOnceAsync(CancellationToken.None));

        Assert.Equal(0, await s.LimitInUseAsync());
        Assert.Contains("group-tasks", kinds);
    }

    /// <summary>Creates <c>{table}_yYYYYmMM</c> for the UTC month <paramref name="monthsBack"/> before the current one.</summary>
    private static async Task<string> CreateMonthPartitionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string table, int monthsBack)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT @table || to_char(m, '"_y"YYYY"m"MM'),
                   format('CREATE TABLE %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                          @table || to_char(m, '"_y"YYYY"m"MM'), @table,
                          m AT TIME ZONE 'UTC', (m + interval '1 month') AT TIME ZONE 'UTC')
            FROM (SELECT date_trunc('month', now() AT TIME ZONE 'UTC') - make_interval(months => @back) AS m) x
            """, conn, tx);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("back", monthsBack);

        string name, ddl;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            name = reader.GetString(0);
            ddl = reader.GetString(1);
        }

        await Execute(conn, tx, ddl);
        return name;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string name)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", conn, tx);
        cmd.Parameters.AddWithValue("name", name);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Execute(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task FinishAsync(Guid runId, int daysAgo) =>
        HostData.ExecuteAsync(pg, """
            UPDATE workflow_runs SET status = 'succeeded', finished_at = now() - make_interval(days => @days)
            WHERE id = @id
            """, ("id", runId), ("days", daysAgo));

    private async Task<int> PurgeAsync()
    {
        await using var conn = await pg.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var purged = await HistoryRetention.PurgeFinishedRunsAsync(conn, tx, CancellationToken.None);
        await tx.CommitAsync();
        return purged;
    }

    private Task<long> CountAsync(string sql, Guid id) => HostData.ScalarAsync<long>(pg, sql, ("id", id));
}
