using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Loops;
using Baton.Persistence.Queue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-09 (lost leases), WF-05 (timeout backstop), NFR-Auditability (history), limit reconciliation.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ReaperTests(PostgresFixture pg)
{
    [Fact]
    public async Task Host_dies_mid_task_attempt_is_lost_and_task_waits_for_retry()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), maxConcurrentTasks: 4);
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await s.ExpireLeaseAsync(lease.TaskRunId);

        await s.Reaper.ReapAsync(CancellationToken.None);

        var row = await s.TaskAsync(runId, "a");
        Assert.Equal("retry_wait", row.Status);
        Assert.Null(row.LeaseHostId);
        Assert.Equal(0, await s.LimitInUseAsync());
        Assert.Equal("lost", await HostData.ScalarAsync<string>(pg,
            "SELECT outcome FROM task_attempts WHERE task_run_id = @id AND attempt = 1", ("id", lease.TaskRunId)));
        Assert.Contains("task.lost", await s.EventTypesAsync(runId, "a"));
    }

    [Fact]
    public async Task Last_attempt_lost_fails_the_task_and_the_run()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").Retry(1, TimeSpan.Zero));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await s.ExpireLeaseAsync(lease.TaskRunId);

        await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("failed", (await s.TaskAsync(runId, "a")).Status);
        Assert.Equal("failed", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Lost_lease_of_a_cancel_requested_task_is_cancelled_not_retried()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET cancel_requested = true WHERE id = @id", ("id", lease.TaskRunId));
        await s.ExpireLeaseAsync(lease.TaskRunId);

        await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("cancelled", (await s.TaskAsync(runId, "a")).Status);
    }

    [Fact]
    public async Task Unresponsive_handler_is_closed_as_timed_out_and_its_later_result_is_stale()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").Timeout(TimeSpan.FromMinutes(1)));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await s.Leases.StartAsync(s.GroupId, lease.TaskRunId, lease.LeaseToken, CancellationToken.None);
        // The host keeps heartbeating (lease valid), but the handler has run 2 min + 1 s past a 1 min timeout.
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET started_at = now() - interval '121 seconds' WHERE id = @id", ("id", lease.TaskRunId));

        await s.Reaper.ReapAsync(CancellationToken.None);
        var late = await s.CompleteAsync(lease);

        Assert.False(late);
        Assert.Equal("retry_wait", (await s.TaskAsync(runId, "a")).Status);
        Assert.Equal("timed_out", await HostData.ScalarAsync<string>(pg,
            "SELECT outcome FROM task_attempts WHERE task_run_id = @id AND attempt = 1", ("id", lease.TaskRunId)));
    }

    [Fact]
    public async Task Handler_within_its_timeout_plus_grace_is_left_alone()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").Timeout(TimeSpan.FromMinutes(1)));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET leased_at = now() - interval '110 seconds' WHERE id = @id", ("id", lease.TaskRunId));

        await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("leased", (await s.TaskAsync(runId, "a")).Status);
    }

    [Fact]
    public async Task Reaper_persists_offline_for_hosts_that_stopped_heartbeating()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var silent = await s.AddHostAsync();
        var alive = await s.AddHostAsync();
        await HostData.AgeHeartbeatAsync(pg, silent, TimeSpan.FromSeconds(31));

        await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("offline", await HostData.ScalarAsync<string>(pg, "SELECT status FROM hosts WHERE id = @id", ("id", silent)));
        Assert.Equal("online", await HostData.ScalarAsync<string>(pg, "SELECT status FROM hosts WHERE id = @id", ("id", alive)));
    }

    [Fact]
    public async Task History_shows_lost_attempt_on_host_a_then_success_on_host_b_with_tokens_and_times()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var hostA = await s.AddHostAsync();
        var hostB = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var first = await s.ClaimOneAsync(hostA);
        await s.ExpireLeaseAsync(first.TaskRunId);
        await s.Reaper.ReapAsync(CancellationToken.None);
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET not_before = now() WHERE id = @id", ("id", first.TaskRunId));
        await s.Promoter.PromoteAsync(CancellationToken.None);
        var second = await s.ClaimOneAsync(hostB);
        await s.CompleteAsync(second);

        var history = new List<(int Attempt, Guid Host, long Token, string Outcome, bool Timed)>();
        await using (var cmd = pg.DataSource.CreateCommand("""
            SELECT attempt, host_id, lease_token, outcome, started_at IS NOT NULL AND finished_at >= started_at
            FROM task_attempts WHERE task_run_id = @id ORDER BY attempt
            """))
        {
            cmd.Parameters.AddWithValue("id", first.TaskRunId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                history.Add((reader.GetInt32(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetBoolean(4)));
        }

        Assert.Equal(
            [(1, hostA, first.LeaseToken, "lost", true), (2, hostB, second.LeaseToken, "succeeded", true)],
            history);
        Assert.Equal(
            ["task.queued", "task.leased", "task.lost", "task.retry_scheduled", "task.queued", "task.leased", "task.succeeded"],
            await s.EventTypesAsync(runId, "a"));
    }

    [Fact]
    public async Task Reconciler_corrects_a_drifted_group_task_counter()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), maxConcurrentTasks: 10);
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        await s.StartRunAsync();
        await s.ClaimAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE concurrency_limits SET in_use = 5 WHERE key = @key", ("key", DbNames.GroupTasksLimit(s.GroupId)));

        var loop = new LimitReconcilerLoop(pg.DataSource, Options.Create(new DispatchOptions()),
            NullLogger<LimitReconcilerLoop>.Instance);
        Assert.True(await loop.RunOnceAsync(CancellationToken.None));

        Assert.Equal(2, await s.LimitInUseAsync());
    }

    [Fact]
    public async Task Reconciler_counts_runs_holding_a_run_limit()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var key = $"group-runs:{s.GroupId}";
        await HostData.ExecuteAsync(pg, "INSERT INTO concurrency_limits (key, max, in_use) VALUES (@key, 5, 0)", ("key", key));
        var runId = await s.StartRunAsync();
        await HostData.ExecuteAsync(pg, "UPDATE workflow_runs SET held_limits = ARRAY[@key] WHERE id = @id", ("key", key), ("id", runId));

        await using (var connection = await pg.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var corrections = await LimitReconciler.ReconcileAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
            Assert.Contains(new LimitCorrection(key, 0, 1), corrections);
        }

        Assert.Equal(1, await HostData.ScalarAsync<int>(pg, "SELECT in_use FROM concurrency_limits WHERE key = @key", ("key", key)));
    }
}
