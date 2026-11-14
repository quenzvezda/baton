using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-12: PinnedRun placement in the claim, drain and reaper paths (M6 tasks 4.2–4.5).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PinnedRunTests(PostgresFixture pg)
{
    [Fact]
    public async Task Pin_labels_are_the_union_of_every_task_label()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf =>
            wf.Task<Noop>("b").RequireLabels("gpu").DependsOn(wf.Task<Noop>("a").RequireLabels("large-memory")));

        var pinned = await s.StartRunAsync(labels: ["region-apac"], pinned: true);
        var perTask = await s.StartRunAsync(labels: ["region-apac"]);

        Assert.Equal(["gpu", "large-memory", "region-apac"], await HostData.ScalarAsync<string[]>(pg,
            "SELECT pin_labels FROM workflow_runs WHERE id = @id", ("id", pinned)));
        Assert.True(await HostData.ScalarAsync<bool>(pg, "SELECT bool_and(pinned_run) FROM task_runs WHERE workflow_run_id = @id", ("id", pinned)));
        Assert.False(await HostData.ScalarAsync<bool>(pg, "SELECT bool_or(pinned_run) FROM task_runs WHERE workflow_run_id = @id", ("id", perTask)));
    }

    private static Task<Guid?> PinnedHostAsync(DispatchScenario s, Guid runId) =>
        HostData.ScalarAsync<Guid?>(s.Pg, "SELECT pinned_host_id FROM workflow_runs WHERE id = @id", ("id", runId));

    private static Task<DispatchScenario> ChainAsync(PostgresFixture pg, string[]? secondLabels = null, int attempts = 1) =>
        DispatchScenario.CreateAsync(pg, wf =>
            wf.Task<Noop>("b").RequireLabels(secondLabels ?? []).DependsOn(wf.Task<Noop>("a").Retry(attempts, TimeSpan.Zero)));

    [Fact]
    public async Task First_claim_pins_the_run()
    {
        var s = await ChainAsync(pg);
        var runId = await s.StartRunAsync(pinned: true);
        var (first, second) = (await s.AddHostAsync(), await s.AddHostAsync());

        var a = await s.ClaimOneAsync(first);
        Assert.Equal(first, await PinnedHostAsync(s, runId));
        Assert.Contains("run.pinned", await s.EventTypesAsync(runId));
        Assert.True(await s.CompleteAsync(a));

        Assert.Empty((await s.ClaimAsync(second)).Tasks);
        Assert.Equal("b", (await s.ClaimOneAsync(first)).TaskKey);
    }

    [Fact]
    public async Task Task_of_a_run_pinned_elsewhere_is_not_leased()
    {
        var s = await ChainAsync(pg);
        var runId = await s.StartRunAsync(pinned: true);
        var (pinnedTo, other) = (await s.AddHostAsync(), await s.AddHostAsync());
        Assert.True(await s.CompleteAsync(await s.ClaimOneAsync(pinnedTo)));

        Assert.Empty((await s.ClaimAsync(other)).Tasks);
        Assert.Equal("queued", (await s.TaskAsync(runId, "b")).Status);
    }

    [Fact]
    public async Task Per_task_runs_are_not_pinned()
    {
        var s = await ChainAsync(pg);
        var runId = await s.StartRunAsync();
        var (first, second) = (await s.AddHostAsync(), await s.AddHostAsync());
        Assert.True(await s.CompleteAsync(await s.ClaimOneAsync(first)));

        Assert.Equal("b", (await s.ClaimOneAsync(second)).TaskKey);
        Assert.Null(await PinnedHostAsync(s, runId));
    }

    [Fact]
    public async Task Pinned_host_must_carry_labels_of_the_whole_run()
    {
        var s = await ChainAsync(pg, secondLabels: ["large-memory"]);
        var runId = await s.StartRunAsync(pinned: true);
        var small = await s.AddHostAsync(labels: []);
        var large = await s.AddHostAsync(labels: ["large-memory"]);

        Assert.Empty((await s.ClaimAsync(small)).Tasks); // "a" alone would fit, but "b" could never follow
        Assert.Equal("a", (await s.ClaimOneAsync(large)).TaskKey);
        Assert.Equal(large, await PinnedHostAsync(s, runId));
    }

    [Fact]
    public async Task Retry_goes_to_the_pinned_host()
    {
        var s = await ChainAsync(pg, attempts: 3);
        var runId = await s.StartRunAsync(pinned: true);
        var (pinnedTo, other) = (await s.AddHostAsync(), await s.AddHostAsync());

        Assert.True(await s.CompleteAsync(await s.ClaimOneAsync(pinnedTo), Baton.Domain.Runs.AttemptOutcome.Failed));
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET not_before = now() - interval '1 second' WHERE workflow_run_id = @id", ("id", runId));
        await s.Promoter.PromoteAsync(CancellationToken.None);

        Assert.Empty((await s.ClaimAsync(other)).Tasks);
        var retry = await s.ClaimOneAsync(pinnedTo);
        Assert.Equal(("a", 2), (retry.TaskKey, retry.Attempt));
    }

    [Fact]
    public async Task Concurrent_claims_of_parallel_roots_pin_one_host()
    {
        const int runs = 150, claimers = 4;
        var s = await DispatchScenario.CreateAsync(pg, wf =>
        {
            wf.Task<Noop>("left");
            wf.Task<Noop>("right");
        });
        var runIds = new List<Guid>();
        for (var i = 0; i < runs; i++)
            runIds.Add(await s.StartRunAsync(pinned: true));
        var hosts = new List<Guid>();
        for (var i = 0; i < claimers; i++)
            hosts.Add(await s.AddHostAsync(slots: runs * 2));

        using var start = new ManualResetEventSlim();
        var work = hosts.Select(host => Task.Run(async () =>
        {
            start.Wait();
            for (var idle = 0; idle < 20;)
            {
                var claimed = (await s.ClaimAsync(host, freeSlots: 3)).Tasks.Count;
                idle = claimed == 0 ? idle + 1 : 0;
            }
        })).ToList();
        start.Set();
        await Task.WhenAll(work);

        // Every task is leased, and both tasks of each run went to the one host the run is pinned to.
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM task_runs WHERE host_group_id = @g AND status <> 'leased'", ("g", s.GroupId)));
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg, """
            SELECT count(*) FROM workflow_runs r
            WHERE r.id = ANY (@ids)
              AND (r.pinned_host_id IS NULL
                   OR EXISTS (SELECT 1 FROM task_runs t WHERE t.workflow_run_id = r.id AND t.lease_host_id IS DISTINCT FROM r.pinned_host_id))
            """, ("ids", runIds.ToArray())));
        Assert.True(await HostData.ScalarAsync<long>(pg,
            "SELECT count(DISTINCT pinned_host_id) FROM workflow_runs WHERE id = ANY (@ids)", ("ids", runIds.ToArray())) > 1,
            "Runs should spread over several hosts.");
    }

    [Fact]
    public async Task Draining_host_takes_tasks_of_its_pinned_run_only()
    {
        var s = await ChainAsync(pg);
        var pinnedRun = await s.StartRunAsync(pinned: true);
        var host = await s.AddHostAsync(slots: 10);
        var a = await s.ClaimOneAsync(host);
        Assert.Equal(pinnedRun, a.RunId);
        var perTask = await s.StartRunAsync();
        var unpinned = await s.StartRunAsync(pinned: true);

        await HostData.ExecuteAsync(pg, "UPDATE hosts SET drain_requested = true WHERE id = @id", ("id", host));
        Assert.Empty((await s.ClaimAsync(host)).Tasks); // nothing of its pinned run is queued yet
        Assert.True(await s.CompleteAsync(a));

        var claimed = (await s.ClaimAsync(host, freeSlots: 10)).Tasks;
        Assert.Equal([(pinnedRun, "b")], claimed.Select(t => (t.RunId, t.TaskKey)));
        Assert.Equal(("queued", "queued"), ((await s.TaskAsync(perTask, "a")).Status, (await s.TaskAsync(unpinned, "a")).Status));
    }

    [Fact]
    public async Task Run_fails_when_its_pinned_host_goes_offline()
    {
        var s = await ChainAsync(pg);
        var runId = await s.StartRunAsync(pinned: true);
        var host = await s.AddHostAsync();
        var a = await s.ClaimOneAsync(host);

        await HostData.AgeHeartbeatAsync(pg, host, TimeSpan.FromSeconds(31));
        var summary = await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal(1, summary.RunsPinnedHostLost);
        Assert.Equal("cancelling", await s.RunStatusAsync(runId));
        Assert.Equal("cancelled", (await s.TaskAsync(runId, "b")).Status); // Waiting: cancelled at once

        await s.ExpireLeaseAsync(a.TaskRunId); // the dead host never reports; its lease runs out
        await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("cancelled", (await s.TaskAsync(runId, "a")).Status);
        Assert.Equal("failed", await s.RunStatusAsync(runId));
        Assert.Equal("pinned host lost", await HostData.ScalarAsync<string>(pg,
            "SELECT data->>'reason' FROM run_events WHERE workflow_run_id = @id AND type = 'run.failed'", ("id", runId)));
    }

    [Fact]
    public async Task Run_fails_when_its_pinned_host_is_moved()
    {
        var s = await ChainAsync(pg);
        var runId = await s.StartRunAsync(pinned: true);
        var host = await s.AddHostAsync();
        Assert.True(await s.CompleteAsync(await s.ClaimOneAsync(host)));
        var (otherGroup, _) = await HostData.CreateGroupAsync(pg);

        await HostData.ExecuteAsync(pg, "UPDATE hosts SET host_group_id = @g WHERE id = @id", ("g", otherGroup), ("id", host));
        await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("failed", await s.RunStatusAsync(runId)); // "b" was only Queued, so the run ends at once
    }

    [Fact]
    public async Task Pinned_host_restarting_within_30_s_keeps_the_run()
    {
        var s = await ChainAsync(pg);
        var runId = await s.StartRunAsync(pinned: true);
        var host = await s.AddHostAsync();
        Assert.True(await s.CompleteAsync(await s.ClaimOneAsync(host)));

        await HostData.AgeHeartbeatAsync(pg, host, TimeSpan.FromSeconds(20));
        await s.Reaper.ReapAsync(CancellationToken.None);

        Assert.Equal("running", await s.RunStatusAsync(runId));
        Assert.Equal("b", (await s.ClaimOneAsync(host)).TaskKey);
    }
}
