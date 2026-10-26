using Baton.Abstractions;
using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-03 (DAG advance in the completing transaction), ORC-10 (retries), WF-07, run outcome (decision 7).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CompletionTests(PostgresFixture pg)
{
    [Fact]
    public async Task Last_parent_succeeding_queues_the_child_in_the_same_step()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf =>
            wf.Task<Noop>("build-report").DependsOn(wf.Task<Noop>("extract-orders"), wf.Task<Noop>("extract-customers")));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var leases = (await s.ClaimAsync(host)).Tasks;

        await s.CompleteAsync(leases[0]);
        Assert.Equal("waiting", (await s.TaskAsync(runId, "build-report")).Status);
        await s.CompleteAsync(leases[1]);

        var build = await s.TaskAsync(runId, "build-report");
        Assert.Equal("queued", build.Status);
        Assert.Equal(0, build.UpstreamRemaining);
        Assert.Contains("task.queued", await s.EventTypesAsync(runId, "build-report"));
    }

    [Fact]
    public async Task Failure_skips_all_success_descendants_and_runs_the_failure_handler()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf =>
        {
            var extract = wf.Task<Noop>("extract").Retry(1, TimeSpan.Zero);
            var build = wf.Task<Noop>("build-report").DependsOn(extract);
            wf.Task<Noop>("publish-report").DependsOn(build);
            wf.Task<Noop>("notify-failure").DependsOn(extract, build).When(TriggerRule.OneFailed);
        });
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();

        await s.CompleteAsync(await s.ClaimOneAsync(host), AttemptOutcome.Failed);

        Assert.Equal("failed", (await s.TaskAsync(runId, "extract")).Status);
        Assert.Equal("skipped", (await s.TaskAsync(runId, "build-report")).Status);
        Assert.Equal("skipped", (await s.TaskAsync(runId, "publish-report")).Status);
        Assert.Equal("queued", (await s.TaskAsync(runId, "notify-failure")).Status);
        Assert.Equal("running", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Run_succeeds_when_its_last_task_succeeds()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf =>
        {
            var a = wf.Task<Noop>("a");
            wf.Task<Noop>("b").DependsOn(a);
            wf.Task<Noop>("on-failure").DependsOn(a).When(TriggerRule.OneFailed);
        });
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();

        await s.CompleteAsync(await s.ClaimOneAsync(host));
        Assert.Equal("running", await s.RunStatusAsync(runId));
        await s.CompleteAsync(await s.ClaimOneAsync(host));

        Assert.Equal("skipped", (await s.TaskAsync(runId, "on-failure")).Status);
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
        Assert.True(await HostData.ScalarAsync<bool>(pg, "SELECT finished_at IS NOT NULL FROM workflow_runs WHERE id = @id", ("id", runId)));
        Assert.Contains("run.succeeded", await s.EventTypesAsync(runId));
    }

    [Fact]
    public async Task Run_fails_when_any_task_failed()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf =>
        {
            var a = wf.Task<Noop>("a").Retry(1, TimeSpan.Zero);
            wf.Task<Noop>("cleanup").DependsOn(a).When(TriggerRule.AllDone);
        });
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();

        await s.CompleteAsync(await s.ClaimOneAsync(host), AttemptOutcome.Failed);
        await s.CompleteAsync(await s.ClaimOneAsync(host));

        Assert.Equal("failed", await s.RunStatusAsync(runId));
        Assert.Contains("run.failed", await s.EventTypesAsync(runId));
    }

    [Fact]
    public async Task Two_last_tasks_completing_concurrently_end_the_run_exactly_once()
    {
        for (var round = 0; round < 10; round++)
        {
            var s = await DispatchScenario.CreateAsync(pg, wf => { wf.Task<Noop>("left"); wf.Task<Noop>("right"); });
            var hostA = await s.AddHostAsync();
            var hostB = await s.AddHostAsync();
            var runId = await s.StartRunAsync();
            var left = await s.ClaimOneAsync(hostA);
            var right = await s.ClaimOneAsync(hostB);

            await Task.WhenAll(Task.Run(() => s.CompleteAsync(left)), Task.Run(() => s.CompleteAsync(right)));

            Assert.Equal("succeeded", await s.RunStatusAsync(runId));
            Assert.Single(await s.EventTypesAsync(runId), t => t.StartsWith("run.s", StringComparison.Ordinal) && t != "run.started");
        }
    }

    [Fact]
    public async Task Crash_before_commit_leaves_no_partial_state()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("b").DependsOn(wf.Task<Noop>("a")), maxConcurrentTasks: 5);
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        var key = DbNames.GroupTasksLimit(s.GroupId);
        var trigger = $"crash_{s.GroupId:N}";
        // Releasing the group slot is one of the last statements of Complete: fail there.
        await HostData.ExecuteAsync(pg, $"""
            CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$;
            CREATE TRIGGER {trigger} BEFORE UPDATE ON concurrency_limits FOR EACH ROW
              WHEN (OLD.key = '{key}' AND NEW.in_use < OLD.in_use) EXECUTE FUNCTION {trigger}();
            """);
        var eventsBefore = await s.EventTypesAsync(runId);

        try
        {
            await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => s.CompleteAsync(lease));
        }
        finally
        {
            await HostData.ExecuteAsync(pg, $"DROP TRIGGER {trigger} ON concurrency_limits; DROP FUNCTION {trigger}();");
        }

        var a = await s.TaskAsync(runId, "a");
        Assert.Equal("leased", a.Status);
        Assert.Null(a.Output);
        Assert.Equal("waiting", (await s.TaskAsync(runId, "b")).Status);
        Assert.Equal(1, await s.LimitInUseAsync());
        Assert.Equal(eventsBefore, await s.EventTypesAsync(runId));
        Assert.True(await s.CompleteAsync(lease));
        Assert.Equal("queued", (await s.TaskAsync(runId, "b")).Status);
    }

    [Fact]
    public async Task Retryable_failure_waits_in_retry_wait_for_the_backoff()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();

        await s.CompleteAsync(await s.ClaimOneAsync(host), AttemptOutcome.Failed);

        var row = await s.TaskAsync(runId, "a");
        Assert.Equal("retry_wait", row.Status);
        var wait = await HostData.ScalarAsync<TimeSpan>(pg, "SELECT not_before - now() FROM task_runs WHERE id = @id", ("id", row.Id));
        Assert.InRange(wait.TotalSeconds, 29.0, 36.0);
        Assert.Null(row.LeaseHostId);
        Assert.Contains("task.retry_scheduled", await s.EventTypesAsync(runId, "a"));
    }

    [Fact]
    public async Task Due_retry_is_queued_again_and_claimable()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        await s.CompleteAsync(await s.ClaimOneAsync(host), AttemptOutcome.TimedOut);
        var row = await s.TaskAsync(runId, "a");
        await s.Promoter.PromoteAsync(CancellationToken.None);
        Assert.Equal("retry_wait", (await s.TaskAsync(runId, "a")).Status);

        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET not_before = now() - interval '1 second' WHERE id = @id", ("id", row.Id));
        await s.Promoter.PromoteAsync(CancellationToken.None);

        Assert.Equal("queued", (await s.TaskAsync(runId, "a")).Status);
        Assert.Equal(2, (await s.ClaimOneAsync(host)).Attempt);
    }

    [Fact]
    public async Task Non_retryable_failure_fails_the_task_at_once()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();

        await s.CompleteAsync(await s.ClaimOneAsync(host), AttemptOutcome.Failed, retryable: false);

        Assert.Equal("failed", (await s.TaskAsync(runId, "a")).Status);
        Assert.Equal("failed", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Output_over_256_KB_fails_the_task_without_retry()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        var huge = $"\"{new string('x', TaskOutput.MaxBytes)}\"";

        await s.CompleteAsync(lease, output: huge);

        var row = await s.TaskAsync(runId, "a");
        Assert.Equal("failed", row.Status);
        Assert.Null(row.Output);
        Assert.Contains("OutputTooLarge", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requested_cancellation_reported_by_the_agent_cancels_the_task()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("b").DependsOn(wf.Task<Noop>("a")));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET cancel_requested = true WHERE id = @id", ("id", lease.TaskRunId));

        await s.CompleteAsync(lease, AttemptOutcome.Cancelled);

        Assert.Equal("cancelled", (await s.TaskAsync(runId, "a")).Status);
        Assert.Equal("skipped", (await s.TaskAsync(runId, "b")).Status);
    }
}
