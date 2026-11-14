using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Baton.Abstractions;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Runs;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Queue;
using Baton.Persistence.Runs;

namespace Baton.Integration.Tests.Runs;

/// <summary>WF-10: re-run a Failed or TimedOut run in place from its failed tasks (M7 group 3).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RerunTests(PostgresFixture pg)
{
    // extract-a, extract-b -> build-report -> publish; notify-failure runs when any of them failed.
    private static void Report(IWorkflowBuilder<NoParams> wf)
    {
        var a = wf.Task<Noop>("extract-a");
        var b = wf.Task<Noop>("extract-b");
        var build = wf.Task<Noop>("build-report").DependsOn(a, b).Retry(2, TimeSpan.Zero);
        wf.Task<Noop>("publish").DependsOn(build);
        wf.Task<Noop>("notify-failure").DependsOn(a, b, build).When(TriggerRule.OneFailed);
    }

    // 3.1 — the request.

    [Fact]
    public async Task Failed_run_is_rerun_as_pending()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var client = api.Client(BatonRoles.Operator);

        var response = await client.PostAsync($"/api/runs/{runId}/retry-failed", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var detail = (await response.Content.ReadFromJsonAsync<RunDetail>())!;
        Assert.Equal("pending", detail.Status);
        Assert.Equal(1, detail.Reruns);
        Assert.Null(detail.FinishedAt);
        Assert.Contains(RunEventTypes.RunRerunRequested, await s.Dispatch.EventTypesAsync(runId));
        Assert.Equal("failed", (await s.Dispatch.TaskAsync(runId, "build-report")).Status);
        Assert.Empty((await s.Dispatch.ClaimAsync(host)).Tasks);
    }

    [Fact]
    public async Task Timed_out_run_is_rerun_and_its_timeout_cleared()
    {
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("b").DependsOn(wf.Task<Noop>("a")), runTimeout: TimeSpan.FromHours(2));
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        var lease = await s.Dispatch.ClaimOneAsync(host);
        await HostData.ExecuteAsync(pg, "UPDATE workflow_runs SET deadline_at = now() - interval '1 second' WHERE id = @id", ("id", runId));
        await s.Dispatch.Reaper.ReapAsync(CancellationToken.None);
        await s.Dispatch.CompleteAsync(lease, AttemptOutcome.Cancelled);
        Assert.Equal("timed_out", await s.RunStatusAsync(runId));

        Assert.Equal(RerunOutcome.Requested, await new RunRerun(pg.DataSource).RequestAsync(runId, "ana", CancellationToken.None));

        Assert.Equal("pending", await s.RunStatusAsync(runId));
        Assert.True(await HostData.ScalarAsync<bool>(pg,
            "SELECT deadline_at IS NULL AND cancel_reason IS NULL AND finished_at IS NULL FROM workflow_runs WHERE id = @id", ("id", runId)));

        await s.AdmitAsync();
        Assert.Equal("running", await s.RunStatusAsync(runId));
        Assert.True(await HostData.ScalarAsync<bool>(pg,
            "SELECT deadline_at > now() + interval '1 hour' FROM workflow_runs WHERE id = @id", ("id", runId)));
        await s.CompleteRunAsync(runId, host);
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("pending")]
    [InlineData("succeeded")]
    [InlineData("cancelled")]
    public async Task Rerun_of_a_run_in_another_state_is_409(string status)
    {
        var s = await RunScenario.CreateAsync(pg, policy: "queue", maxRuns: 10);
        var host = await s.Dispatch.AddHostAsync();
        var runId = status == "pending" ? await s.CreateRunAsync() : await s.StartRunAsync();
        if (status == "succeeded")
            await s.CompleteRunAsync(runId, host);
        if (status == "cancelled")
            await s.Cancellation.CancelAsync(runId, RunCancelReason.User, CancellationToken.None);
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var client = api.Client(BatonRoles.Operator);

        var response = await client.PostAsync($"/api/runs/{runId}/retry-failed", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await RerunsAsync(runId));
        if (status == "running")
            await s.CompleteRunAsync(runId, host);
    }

    [Fact]
    public async Task Viewer_cannot_rerun()
    {
        var (_, _, runId) = await FailedReportRunAsync();
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var viewer = api.Client(BatonRoles.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/runs/{runId}/retry-failed", null)).StatusCode);
        Assert.Equal("failed", await HostData.ScalarAsync<string>(pg, "SELECT status FROM workflow_runs WHERE id = @id", ("id", runId)));
    }

    // 3.2 — re-arming at admission.

    [Fact]
    public async Task Rerun_queues_the_failed_task_with_kept_upstream_outputs()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        await RerunAndAdmitAsync(s, runId);

        Assert.Equal("queued", (await s.Dispatch.TaskAsync(runId, "build-report")).Status);
        Assert.Equal("waiting", (await s.Dispatch.TaskAsync(runId, "publish")).Status);
        var lease = await s.Dispatch.ClaimOneAsync(host);
        Assert.Equal("build-report", lease.TaskKey);
        Assert.Equal(["extract-a", "extract-b"], lease.UpstreamOutputs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("\"a-out\"", lease.UpstreamOutputs["extract-a"]);
    }

    [Fact]
    public async Task Succeeded_tasks_are_not_run_again()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        var extractBefore = await s.Dispatch.TaskAsync(runId, "extract-a");
        var notifyBefore = await s.Dispatch.TaskAsync(runId, "notify-failure");

        await RerunAndAdmitAsync(s, runId);
        await s.CompleteRunAsync(runId, host);

        Assert.Equal(extractBefore, await s.Dispatch.TaskAsync(runId, "extract-a"));
        Assert.Equal(notifyBefore, await s.Dispatch.TaskAsync(runId, "notify-failure"));
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Rerun_attempts_continue_with_a_fresh_budget()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        await RerunAndAdmitAsync(s, runId);

        var third = await s.Dispatch.ClaimOneAsync(host);
        Assert.Equal(3, third.Attempt);
        await s.Dispatch.CompleteAsync(third, AttemptOutcome.Failed);
        await s.Dispatch.Promoter.PromoteAsync(CancellationToken.None);
        var fourth = await s.Dispatch.ClaimOneAsync(host);
        Assert.Equal(4, fourth.Attempt);
        await s.Dispatch.CompleteAsync(fourth, AttemptOutcome.Failed);

        Assert.Equal("failed", (await s.Dispatch.TaskAsync(runId, "build-report")).Status);
        Assert.Equal("failed", await s.RunStatusAsync(runId));
        var detail = (await new RunQueries(pg.DataSource).FindAsync(runId, CancellationToken.None))!;
        Assert.Equal([1, 2, 3, 4], detail.Tasks.Single(t => t.Key == "build-report").Attempts.Select(a => a.Attempt));
    }

    [Fact]
    public async Task Rerun_keeps_the_idempotency_key()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        await RerunAndAdmitAsync(s, runId);

        var lease = await s.Dispatch.ClaimOneAsync(host);

        // TaskContext.IdempotencyKey is "{RunId}:{TaskKey}"; both are unchanged by a re-run.
        Assert.Equal(runId, lease.RunId);
        Assert.Equal("build-report", lease.TaskKey);
        Assert.Equal((await s.Dispatch.TaskAsync(runId, "build-report")).Id, lease.TaskRunId);
    }

    [Fact]
    public async Task Rerun_uses_the_original_definition_version()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        await DispatchScenario.RegisterAsync(pg, new DynamicWorkflow(s.WorkflowKey, version: 2));

        await RerunAndAdmitAsync(s, runId);

        var lease = await s.Dispatch.ClaimOneAsync(host);
        Assert.Equal(s.Dispatch.Hash, lease.DefinitionHash);
    }

    [Fact]
    public async Task Rerun_waits_for_the_automation_run_limit()
    {
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), policy: "queue", maxRuns: 1);
        var host = await s.Dispatch.AddHostAsync();
        var failed = await s.StartRunAsync();
        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host), AttemptOutcome.Failed, retryable: false);
        var other = await s.StartRunAsync();

        await new RunRerun(pg.DataSource).RequestAsync(failed, "ana", CancellationToken.None);
        await s.AdmitAsync();
        Assert.Equal("pending", await s.RunStatusAsync(failed));

        await s.CompleteRunAsync(other, host);
        await s.AdmitAsync();
        Assert.Equal("running", await s.RunStatusAsync(failed));
        await s.CompleteRunAsync(failed, host);
        Assert.Equal("succeeded", await s.RunStatusAsync(failed));
    }

    [Fact]
    public async Task Pinned_rerun_pins_again()
    {
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("b").DependsOn(wf.Task<Noop>("a")));
        await HostData.ExecuteAsync(pg, "UPDATE automations SET placement_mode = 'pinned_run' WHERE id = @id", ("id", s.AutomationId));
        var first = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(first), AttemptOutcome.Failed, retryable: false);
        Assert.Equal(first, await PinnedHostAsync(runId));

        await RerunAndAdmitAsync(s, runId);
        Assert.Null(await PinnedHostAsync(runId));
        await HostData.ExecuteAsync(pg, "UPDATE hosts SET drain_requested = true WHERE id = @id", ("id", first));
        var second = await s.Dispatch.AddHostAsync();
        await s.CompleteRunAsync(runId, second);

        Assert.Equal(second, await PinnedHostAsync(runId));
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
    }

    // 3.3 — outcome after a re-run.

    [Fact]
    public async Task Rerun_that_succeeds_ends_succeeded_and_releases_limits()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        await RerunAndAdmitAsync(s, runId);
        Assert.Equal(1, await s.InUseAsync(s.AutomationLimit));

        await s.CompleteRunAsync(runId, host);

        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
        Assert.Equal("succeeded", (await s.Dispatch.TaskAsync(runId, "publish")).Status);
        Assert.Equal(0, await s.InUseAsync(s.AutomationLimit));
    }

    [Fact]
    public async Task Rerun_that_fails_again_can_be_rerun_again()
    {
        var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();
        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host), AttemptOutcome.Failed, retryable: false);

        await RerunAndAdmitAsync(s, runId);
        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host), AttemptOutcome.Failed, retryable: false);
        Assert.Equal("failed", await s.RunStatusAsync(runId));

        await RerunAndAdmitAsync(s, runId);
        var third = await s.Dispatch.ClaimOneAsync(host);
        Assert.Equal(3, third.Attempt);
        await s.Dispatch.CompleteAsync(third);
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
        Assert.Equal(2, await RerunsAsync(runId));
    }

    // 3.4 — concurrency.

    [Fact]
    public async Task Two_simultaneous_reruns_give_one_202_and_one_409()
    {
        var (_, _, runId) = await FailedReportRunAsync();
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var a = api.Client(BatonRoles.Operator);
        using var b = api.Client(BatonRoles.Operator);

        var responses = await Task.WhenAll(
            a.PostAsync($"/api/runs/{runId}/retry-failed", null),
            b.PostAsync($"/api/runs/{runId}/retry-failed", null));

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        Assert.Equal(1, await RerunsAsync(runId));
    }

    [Fact]
    public async Task Rerun_admitted_during_claims_leases_each_reset_root_once()
    {
        const int Roots = 8;
        var s = await RunScenario.CreateAsync(pg, wf =>
        {
            for (var i = 0; i < Roots; i++)
                wf.Task<Noop>($"root-{i}");
        });
        var hosts = new List<Guid>();
        for (var i = 0; i < 4; i++)
            hosts.Add(await s.Dispatch.AddHostAsync(slots: 2));
        var runId = await s.StartRunAsync();
        for (var i = 0; i < Roots; i++)
            await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(hosts[0]), AttemptOutcome.Failed, retryable: false);
        Assert.Equal("failed", await s.RunStatusAsync(runId));
        await new RunRerun(pg.DataSource).RequestAsync(runId, "ana", CancellationToken.None);

        var leased = new ConcurrentBag<ClaimedTask>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var claimers = hosts.Select(host => Task.Run(async () =>
        {
            while (leased.Count < Roots && !stop.IsCancellationRequested)
            {
                foreach (var task in (await s.Dispatch.ClaimAsync(host, 2)).Tasks)
                    leased.Add(task);
            }
        })).ToList();
        await s.AdmitAsync();
        await Task.WhenAll(claimers);

        Assert.Equal(Roots, leased.Count);
        Assert.Equal(Roots, leased.Select(t => t.TaskKey).Distinct().Count());
        Assert.All(leased, t => Assert.Equal(2, t.Attempt));
        foreach (var task in leased)
            await s.Dispatch.CompleteAsync(task);
        Assert.Equal("succeeded", await s.RunStatusAsync(runId));
    }

    [Fact]
    public async Task Rerun_racing_the_reaper_is_rejected_or_rearms_after_it()
    {
        for (var round = 0; round < 5; round++)
        {
            var s = await RunScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").Retry(1, TimeSpan.Zero));
            var host = await s.Dispatch.AddHostAsync();
            var runId = await s.StartRunAsync();
            var lease = await s.Dispatch.ClaimOneAsync(host);
            await s.Dispatch.ExpireLeaseAsync(lease.TaskRunId);

            var rerun = new RunRerun(pg.DataSource);
            var reap = s.Dispatch.Reaper.ReapAsync(CancellationToken.None);
            var request = rerun.RequestAsync(runId, "ana", CancellationToken.None);
            await reap;
            var outcome = await request;

            if (outcome == RerunOutcome.NotRerunnable)
            {
                Assert.Equal("failed", await s.RunStatusAsync(runId));
                Assert.Equal(RerunOutcome.Requested, await rerun.RequestAsync(runId, "ana", CancellationToken.None));
            }

            Assert.Equal("pending", await s.RunStatusAsync(runId));
            await s.AdmitAsync();
            var again = await s.Dispatch.ClaimOneAsync(host);
            Assert.Equal(2, again.Attempt);
            await s.Dispatch.CompleteAsync(again);
            Assert.Equal("succeeded", await s.RunStatusAsync(runId));
        }
    }

    // 3.5 — run detail.

    [Fact]
    public async Task Rerun_run_detail_shows_count_and_all_attempts()
    {
        var (s, host, runId) = await FailedReportRunAsync();
        await RerunAndAdmitAsync(s, runId);
        await s.CompleteRunAsync(runId, host);
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var viewer = api.Client(BatonRoles.Viewer);

        var detail = (await viewer.GetFromJsonAsync<RunDetail>($"/api/runs/{runId}"))!;

        Assert.Equal(1, detail.Reruns);
        Assert.Equal("succeeded", detail.Status);
        var build = detail.Tasks.Single(t => t.Key == "build-report");
        Assert.Equal(["failed", "failed", "succeeded"], build.Attempts.Select(a => a.Outcome));
    }

    /// <summary>A Report run whose extracts succeeded and whose build-report failed both its attempts.</summary>
    private async Task<(RunScenario S, Guid Host, Guid RunId)> FailedReportRunAsync()
    {
        var s = await RunScenario.CreateAsync(pg, Report);
        var host = await s.Dispatch.AddHostAsync();
        var runId = await s.StartRunAsync();

        foreach (var task in (await s.Dispatch.ClaimAsync(host)).Tasks)
            await s.Dispatch.CompleteAsync(task, output: task.TaskKey == "extract-a" ? "\"a-out\"" : "\"b-out\"");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await s.Dispatch.Promoter.PromoteAsync(CancellationToken.None);
            var build = await s.Dispatch.ClaimOneAsync(host);
            Assert.Equal("build-report", build.TaskKey);
            await s.Dispatch.CompleteAsync(build, AttemptOutcome.Failed);
        }

        await s.Dispatch.CompleteAsync(await s.Dispatch.ClaimOneAsync(host));
        Assert.Equal("succeeded", (await s.Dispatch.TaskAsync(runId, "notify-failure")).Status);
        Assert.Equal("skipped", (await s.Dispatch.TaskAsync(runId, "publish")).Status);
        Assert.Equal("failed", await s.RunStatusAsync(runId));
        return (s, host, runId);
    }

    private static async Task RerunAndAdmitAsync(RunScenario s, Guid runId)
    {
        Assert.Equal(RerunOutcome.Requested, await new RunRerun(s.Pg.DataSource).RequestAsync(runId, "ana", CancellationToken.None));
        await s.AdmitAsync();
        Assert.Equal("running", await s.RunStatusAsync(runId));
    }

    private Task<int> RerunsAsync(Guid runId) =>
        HostData.ScalarAsync<int>(pg, "SELECT reruns FROM workflow_runs WHERE id = @id", ("id", runId));

    private async Task<Guid?> PinnedHostAsync(Guid runId)
    {
        await using var cmd = pg.DataSource.CreateCommand("SELECT pinned_host_id FROM workflow_runs WHERE id = @id");
        cmd.Parameters.AddWithValue("id", runId);
        return await cmd.ExecuteScalarAsync() is Guid id ? id : null;
    }
}
