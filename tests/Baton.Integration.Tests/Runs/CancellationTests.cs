using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Runs;

/// <summary>ORC-11: run cancellation (M4 task 3.5, exit criterion: running, unresponsive host, already finished).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CancellationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Cancel_with_a_running_task_cancels_queued_tasks_and_signals_the_host_on_its_next_heartbeat()
    {
        var s = await RunScenario.CreateAsync(pg, ThreeRoots);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);
        await s.Dispatch.Leases.StartAsync(s.GroupId, lease.TaskRunId, lease.LeaseToken, CancellationToken.None);

        var outcome = await s.Cancellation.CancelAsync(runId, RunCancelReason.User, CancellationToken.None);
        var heartbeat = await s.Dispatch.Leases.RenewAsync(host, [new HeldLease(lease.TaskRunId, lease.LeaseToken)], CancellationToken.None);

        Assert.Equal(CancelOutcome.Cancelling, outcome);
        Assert.Equal("cancelling", await s.RunStatusAsync(runId));
        Assert.Equal(2, await CountAsync(runId, "cancelled"));
        Assert.Equal([lease.TaskRunId], heartbeat.CancelRequested);

        // The host stops the handler and reports Cancelled: final, not retried, and the run ends.
        Assert.True(await s.Dispatch.CompleteAsync(lease, AttemptOutcome.Cancelled));
        Assert.Equal(3, await CountAsync(runId, "cancelled"));
        Assert.Equal("cancelled", await s.RunStatusAsync(runId));
        Assert.Equal(0, await s.InUseAsync(s.AutomationLimit));
    }

    [Fact]
    public async Task Host_that_does_not_respond_has_its_task_cancelled_at_lease_expiry_without_retry()
    {
        var s = await RunScenario.CreateAsync(pg);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);

        await s.Cancellation.CancelAsync(runId, RunCancelReason.User, CancellationToken.None);
        await s.Dispatch.ExpireLeaseAsync(lease.TaskRunId);
        await s.Dispatch.Reaper.ReapAsync(CancellationToken.None);

        var task = await s.Dispatch.TaskAsync(runId, "a");
        Assert.Equal("cancelled", task.Status);
        Assert.Equal(1, task.Attempt);
        Assert.Equal("cancelled", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Handler_that_ignores_cancellation_is_closed_after_the_grace_period_while_its_host_heartbeats()
    {
        var s = await RunScenario.CreateAsync(pg);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);
        await s.Cancellation.CancelAsync(runId, RunCancelReason.User, CancellationToken.None);

        // Within the grace the host renews and is told to stop; the reaper leaves the task alone.
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET cancel_requested_at = now() - interval '29 seconds' WHERE id = @id", ("id", lease.TaskRunId));
        await s.Dispatch.Reaper.ReapAsync(CancellationToken.None);
        Assert.Equal("leased", (await s.Dispatch.TaskAsync(runId, "a")).Status);

        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET cancel_requested_at = now() - interval '31 seconds' WHERE id = @id", ("id", lease.TaskRunId));
        var reaped = await s.Dispatch.Reaper.ReapAsync(CancellationToken.None);
        var heartbeat = await s.Dispatch.Leases.RenewAsync(host, [new HeldLease(lease.TaskRunId, lease.LeaseToken)], CancellationToken.None);

        Assert.Equal(1, reaped.CancelledAfterGrace);
        Assert.Equal("cancelled", (await s.Dispatch.TaskAsync(runId, "a")).Status);
        Assert.Equal("cancelled", await s.RunStatusAsync(runId));
        Assert.Equal([lease.TaskRunId], heartbeat.Lost);
        Assert.False(await s.Dispatch.CompleteAsync(lease)); // a late result is stale
    }

    [Fact]
    public async Task Cancelling_a_pending_run_ends_it_at_once()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "queue");
        await s.StartRunAsync();
        var pending = await s.CreateRunAsync();

        Assert.Equal(CancelOutcome.Cancelled, await s.Cancellation.CancelAsync(pending, RunCancelReason.User, CancellationToken.None));
        Assert.Equal("cancelled", await s.RunStatusAsync(pending));
        Assert.Equal(1, await s.InUseAsync(s.AutomationLimit));
    }

    [Fact]
    public async Task Cancelling_a_finished_run_changes_nothing()
    {
        var s = await RunScenario.CreateAsync(pg);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        await s.CompleteRunAsync(runId, host);

        Assert.Equal(CancelOutcome.AlreadyFinished, await s.Cancellation.CancelAsync(runId, RunCancelReason.User, CancellationToken.None));
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Cancelling_twice_is_harmless()
    {
        var s = await RunScenario.CreateAsync(pg);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        await s.Dispatch.ClaimOneAsync(host);

        await s.Cancellation.CancelAsync(runId, RunCancelReason.User, CancellationToken.None);

        Assert.Equal(CancelOutcome.AlreadyCancelling, await s.Cancellation.CancelAsync(runId, RunCancelReason.User, CancellationToken.None));
    }

    private static void ThreeRoots(Baton.Abstractions.IWorkflowBuilder<NoParams> wf)
    {
        wf.Task<Noop>("a");
        wf.Task<Noop>("b");
        wf.Task<Noop>("c");
    }

    private Task<long> CountAsync(Guid runId, string status) => HostData.ScalarAsync<long>(pg,
        "SELECT count(*) FROM task_runs WHERE workflow_run_id = @id AND status = @status", ("id", runId), ("status", status));
}
