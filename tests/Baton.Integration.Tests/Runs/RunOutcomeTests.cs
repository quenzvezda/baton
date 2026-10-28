using Baton.Abstractions;
using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Orchestrator.Dispatch;
using Baton.Orchestrator.Loops;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Baton.Integration.Tests.Runs;

/// <summary>"Run outcome" (M4 task 3.3), AUT-09 run timeout (3.4), limit reconciliation (3.6), NFR-Auditability timeline (3.7).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RunOutcomeTests(PostgresFixture pg)
{
    [Fact]
    public async Task Only_skips_besides_successes_make_the_run_succeed()
    {
        var s = await RunScenario.CreateAsync(pg, wf =>
        {
            var work = wf.Task<Noop>("work");
            wf.Task<Noop>("notify-failure").DependsOn(work).When(TriggerRule.OneFailed);
        });
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();

        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host));

        Assert.Equal("skipped", (await s.Dispatch.TaskAsync(runId, "notify-failure")).Status);
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
        Assert.Equal(0, await s.InUseAsync(s.AutomationLimit));
    }

    [Fact]
    public async Task One_failure_fails_the_run_and_releases_every_limit_it_held()
    {
        var s = await RunScenario.CreateAsync(pg, wf =>
        {
            wf.Task<Noop>("a").Retry(1, TimeSpan.Zero);
            wf.Task<Noop>("b");
        }, groupMaxRuns: 3, workflowMaxRuns: 3);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        Assert.Equal(1, await s.InUseAsync(s.WorkflowLimit));

        foreach (var task in (await s.Dispatch.ClaimAsync(host)).Tasks)
        {
            var key = await HostData.ScalarAsync<string>(pg, "SELECT task_key FROM task_runs WHERE id = @id", ("id", task.TaskRunId));
            await s.Dispatch.CompleteAsync(task, key == "a" ? AttemptOutcome.Failed : AttemptOutcome.Succeeded, retryable: false);
        }

        Assert.Equal("failed", await s.RunStatusAsync(runId));
        Assert.Equal(0, await s.InUseAsync(s.AutomationLimit));
        Assert.Equal(0, await s.InUseAsync(s.WorkflowLimit));
        Assert.Equal(0, await s.InUseAsync(s.GroupRunsLimit));
    }

    [Fact]
    public async Task Run_past_its_deadline_is_cancelled_and_ends_timed_out()
    {
        var s = await RunScenario.CreateAsync(pg, wf =>
        {
            var a = wf.Task<Noop>("a");
            wf.Task<Noop>("b").DependsOn(a);
        }, runTimeout: TimeSpan.FromHours(2));
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE workflow_runs SET deadline_at = now() - interval '1 second' WHERE id = @id", ("id", runId));

        var reaped = await s.Dispatch.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal(1, reaped.RunsTimedOut);
        Assert.Equal("cancelling", await s.RunStatusAsync(runId));
        Assert.Equal("cancelled", (await s.Dispatch.TaskAsync(runId, "b")).Status);

        await s.Dispatch.CompleteAsync(lease, AttemptOutcome.Cancelled);

        Assert.Equal("timed_out", await s.RunStatusAsync(runId));
        Assert.Equal(0, await s.InUseAsync(s.AutomationLimit));
        Assert.Contains("run.timed_out", await s.Dispatch.EventTypesAsync(runId));
    }

    [Fact]
    public async Task Run_within_its_deadline_is_left_alone()
    {
        var s = await RunScenario.CreateAsync(pg, runTimeout: TimeSpan.FromHours(2));
        var runId = await s.StartRunAsync();

        await s.Dispatch.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("running", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Reconciler_corrects_a_drifted_automation_counter_from_admitted_runs()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "queue", maxRuns: 2);
        await s.StartRunAsync();
        await HostData.ExecuteAsync(pg, "UPDATE concurrency_limits SET in_use = 2 WHERE key = @key", ("key", s.AutomationLimit));

        // A drifted counter blocks admission until the reconciler fixes it.
        var waiting = await s.CreateRunAsync();
        await s.AdmitAsync();
        Assert.Equal("pending", await s.RunStatusAsync(waiting));

        var loop = new LimitReconcilerLoop(pg.DataSource, Options.Create(new DispatchOptions()), NullLogger<LimitReconcilerLoop>.Instance);
        Assert.True(await loop.RunOnceAsync(CancellationToken.None));
        await s.AdmitAsync();

        Assert.Equal("running", await s.RunStatusAsync(waiting));
        Assert.Equal(2, await s.InUseAsync(s.AutomationLimit));
    }

    [Fact]
    public async Task Timeline_of_a_queued_run_shows_created_admitted_started_and_succeeded_in_order()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "queue", maxRuns: 1);
        var host = await s.Dispatch.AddHostAsync();
        var first = await s.StartRunAsync();
        var queued = await s.CreateRunAsync();
        await s.AdmitAsync();
        await s.AdmitAsync();

        await s.CompleteRunAsync(first, host);
        await s.AdmitAsync();
        await s.CompleteRunAsync(queued, host);

        var events = new List<(string Type, DateTime At)>();
        await using (var cmd = pg.DataSource.CreateCommand(
            "SELECT type, at FROM run_events WHERE workflow_run_id = @id AND task_key IS NULL ORDER BY id"))
        {
            cmd.Parameters.AddWithValue("id", queued);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                events.Add((reader.GetString(0), reader.GetDateTime(1)));
        }

        // Passed over once while the first run held the automation limit; recorded once, not per pass.
        Assert.Equal(["run.created", "run.blocked", "run.admitted", "run.started", "run.succeeded"], events.Select(e => e.Type));
        Assert.Equal(events.Select(e => e.At).Order(), events.Select(e => e.At));
    }
}
