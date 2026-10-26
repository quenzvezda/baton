using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-07 (renewal) and ORC-08 (fenced Start, Complete, AppendLogs; ADR-0006).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class FencingTests(PostgresFixture pg)
{
    [Fact]
    public async Task Heartbeat_renews_the_lease_from_database_time()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET lease_expires_at = now() + interval '5 seconds' WHERE id = @id", ("id", lease.TaskRunId));

        var renewal = await s.Leases.RenewAsync(host, [new HeldLease(lease.TaskRunId, lease.LeaseToken)], CancellationToken.None);

        Assert.Empty(renewal.Lost);
        Assert.Empty(renewal.CancelRequested);
        var left = await HostData.ScalarAsync<TimeSpan>(pg, "SELECT lease_expires_at - now() FROM task_runs WHERE id = @id", ("id", lease.TaskRunId));
        Assert.InRange(left, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(60));
        Assert.Equal("leased", (await s.TaskAsync(runId, "a")).Status);
    }

    [Fact]
    public async Task Heartbeat_with_a_wrong_token_reports_the_task_lost_and_renews_nothing()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        var before = await HostData.ScalarAsync<DateTime>(pg, "SELECT lease_expires_at FROM task_runs WHERE id = @id", ("id", lease.TaskRunId));

        var renewal = await s.Leases.RenewAsync(host, [new HeldLease(lease.TaskRunId, lease.LeaseToken - 1)], CancellationToken.None);

        Assert.Equal([lease.TaskRunId], renewal.Lost);
        Assert.Equal(before, await HostData.ScalarAsync<DateTime>(pg, "SELECT lease_expires_at FROM task_runs WHERE id = @id", ("id", lease.TaskRunId)));
    }

    [Fact]
    public async Task Expired_lease_not_yet_reaped_is_lost_and_its_result_is_stale()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await s.ExpireLeaseAsync(lease.TaskRunId);

        var renewal = await s.Leases.RenewAsync(host, [new HeldLease(lease.TaskRunId, lease.LeaseToken)], CancellationToken.None);
        var completed = await s.CompleteAsync(lease);

        Assert.Equal([lease.TaskRunId], renewal.Lost);
        Assert.False(completed);
        Assert.Equal("leased", (await s.TaskAsync(runId, "a")).Status);
    }

    [Fact]
    public async Task Heartbeat_reply_lists_tasks_whose_cancellation_was_requested()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET cancel_requested = true WHERE id = @id", ("id", lease.TaskRunId));

        var renewal = await s.Leases.RenewAsync(host, [new HeldLease(lease.TaskRunId, lease.LeaseToken)], CancellationToken.None);

        Assert.Empty(renewal.Lost);
        Assert.Equal([lease.TaskRunId], renewal.CancelRequested);
    }

    [Fact]
    public async Task Start_moves_the_task_to_running_once()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);

        Assert.True(await s.Leases.StartAsync(s.GroupId, lease.TaskRunId, lease.LeaseToken, CancellationToken.None));
        Assert.True(await s.Leases.StartAsync(s.GroupId, lease.TaskRunId, lease.LeaseToken, CancellationToken.None));

        Assert.Equal("running", (await s.TaskAsync(runId, "a")).Status);
        Assert.Single(await s.EventTypesAsync(runId, "a"), "task.started");
    }

    [Fact]
    public async Task Stale_token_start_complete_and_logs_are_rejected_without_state_change()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        var stale = lease.LeaseToken - 1;
        var eventsBefore = await s.EventTypesAsync(runId);

        var started = await s.Leases.StartAsync(s.GroupId, lease.TaskRunId, stale, CancellationToken.None);
        var completed = await s.Leases.CompleteAsync(s.GroupId,
            DispatchScenario.Report(lease.TaskRunId, stale, AttemptOutcome.Succeeded), CancellationToken.None);
        var logged = await s.Leases.AppendLogsAsync(s.GroupId, lease.TaskRunId, 1, stale,
            [new TaskLogLine(1, DateTime.UtcNow, 2, "late")], CancellationToken.None);

        Assert.False(started);
        Assert.False(completed);
        Assert.Null(logged);
        var row = await s.TaskAsync(runId, "a");
        Assert.Equal("leased", row.Status);
        Assert.Null(row.Output);
        Assert.Equal(eventsBefore, await s.EventTypesAsync(runId));
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM task_logs WHERE task_run_id = @id", ("id", lease.TaskRunId)));
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM task_attempts WHERE task_run_id = @id AND finished_at IS NOT NULL", ("id", lease.TaskRunId)));
    }

    [Fact]
    public async Task Credential_of_another_group_cannot_complete_with_a_guessed_token()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var other = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);

        var completed = await s.Leases.CompleteAsync(other.GroupId,
            DispatchScenario.Report(lease.TaskRunId, lease.LeaseToken, AttemptOutcome.Succeeded), CancellationToken.None);

        Assert.False(completed);
        Assert.Equal("leased", (await s.TaskAsync(runId, "a")).Status);
    }

    [Fact]
    public async Task Late_result_after_reassignment_is_stale_and_only_the_new_attempt_counts()
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

        var late = await s.CompleteAsync(first, output: "\"from A\"");
        var current = await s.CompleteAsync(second, output: "\"from B\"");

        Assert.False(late);
        Assert.True(current);
        var row = await s.TaskAsync(runId, "a");
        Assert.Equal("succeeded", row.Status);
        Assert.Equal("\"from B\"", row.Output);
    }

    [Fact]
    public async Task Log_lines_are_stored_in_sequence_and_a_retried_chunk_is_not_duplicated()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        var at = DateTime.UtcNow;
        TaskLogLine[] chunk = [new(1, at, 2, "one"), new(2, at, 3, "two"), new(3, at.AddYears(-3), 4, "clock skew")];

        var first = await s.Leases.AppendLogsAsync(s.GroupId, lease.TaskRunId, 1, lease.LeaseToken, chunk, CancellationToken.None);
        var retried = await s.Leases.AppendLogsAsync(s.GroupId, lease.TaskRunId, 1, lease.LeaseToken, chunk[..2], CancellationToken.None);

        Assert.Equal(3, first);
        Assert.Equal(0, retried);
        Assert.Equal("one|two|clock skew", await HostData.ScalarAsync<string>(pg,
            "SELECT string_agg(message, '|' ORDER BY seq) FROM task_logs WHERE task_run_id = @id", ("id", lease.TaskRunId)));
        Assert.True(await HostData.ScalarAsync<bool>(pg,
            "SELECT logged_at > now() - interval '1 hour' FROM task_logs WHERE task_run_id = @id AND seq = 3", ("id", lease.TaskRunId)));
    }
}
