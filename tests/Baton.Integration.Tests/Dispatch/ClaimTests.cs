using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-03 (roots), ORC-04 (eligibility), ORC-05 (order), ORC-07 (tokens), HST-01 (group limit).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ClaimTests(PostgresFixture pg)
{
    [Fact]
    public async Task Run_start_queues_root_tasks_and_leaves_the_rest_waiting()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("b").DependsOn(wf.Task<Noop>("a")));

        var runId = await s.StartRunAsync();

        Assert.Equal("queued", (await s.TaskAsync(runId, "a")).Status);
        var b = await s.TaskAsync(runId, "b");
        Assert.Equal("waiting", b.Status);
        Assert.Equal(1, b.UpstreamRemaining);
    }

    [Fact]
    public async Task Eligible_host_leases_the_task_with_a_fencing_token()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync(parameters: """{"region":"eu"}""");

        var lease = await s.ClaimOneAsync(host);

        var row = await s.TaskAsync(runId, "a");
        Assert.Equal("leased", row.Status);
        Assert.Equal(1, row.Attempt);
        Assert.Equal(lease.LeaseToken, row.LeaseToken);
        Assert.Equal(host, row.LeaseHostId);
        Assert.Equal(1, lease.Attempt);
        Assert.Equal("""{"region": "eu"}""", lease.ParametersJson);
        Assert.Equal(TimeSpan.FromHours(1), lease.Timeout);
        Assert.Contains("task.leased", await s.EventTypesAsync(runId, "a"));
    }

    [Fact]
    public async Task Missing_label_leaves_the_task_queued()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").RequireLabels("large-memory"));
        var host = await s.AddHostAsync(labels: ["ssd"]);
        var runId = await s.StartRunAsync();

        Assert.Empty((await s.ClaimAsync(host)).Tasks);
        Assert.Equal("queued", (await s.TaskAsync(runId, "a")).Status);
    }

    [Fact]
    public async Task Automation_labels_are_required_too()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").RequireLabels("ssd"));
        var plain = await s.AddHostAsync(labels: ["ssd"]);
        var gpu = await s.AddHostAsync(labels: ["ssd", "gpu"]);
        await s.StartRunAsync(labels: ["gpu"]);

        Assert.Empty((await s.ClaimAsync(plain)).Tasks);
        Assert.Single((await s.ClaimAsync(gpu)).Tasks);
    }

    [Fact]
    public async Task Missing_definition_version_leaves_the_task_queued()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync(carriesDefinition: false);
        await s.StartRunAsync();

        Assert.Empty((await s.ClaimAsync(host)).Tasks);
    }

    [Fact]
    public async Task Host_of_another_group_receives_nothing()
    {
        var etl = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var gpu = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var gpuHost = await HostData.InsertHostAsync(pg, gpu.GroupId, hashes: [etl.Hash, gpu.Hash]);
        await etl.StartRunAsync();

        Assert.Empty((await gpu.ClaimAsync(gpuHost)).Tasks);
    }

    [Theory]
    [InlineData("drain")]
    [InlineData("shutting-down")]
    [InlineData("offline")]
    public async Task Host_that_is_not_online_receives_nothing(string why)
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        switch (why)
        {
            case "drain":
                await HostData.ExecuteAsync(pg, "UPDATE hosts SET drain_requested = true WHERE id = @id", ("id", host));
                break;
            case "shutting-down":
                await HostData.ExecuteAsync(pg, "UPDATE hosts SET status = 'draining' WHERE id = @id", ("id", host));
                break;
            default:
                await HostData.AgeHeartbeatAsync(pg, host, TimeSpan.FromSeconds(31));
                break;
        }

        Assert.Empty((await s.ClaimAsync(host)).Tasks);
    }

    [Fact]
    public async Task Host_with_every_slot_leased_receives_nothing_whatever_the_agent_says()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync(slots: 4);
        for (var i = 0; i < 5; i++)
            await s.StartRunAsync();

        Assert.Equal(4, (await s.ClaimAsync(host, freeSlots: 10)).Tasks.Count);
        Assert.Empty((await s.ClaimAsync(host, freeSlots: 4)).Tasks);
    }

    [Fact]
    public async Task Claim_never_exceeds_the_free_slots_the_agent_reports()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync(slots: 8);
        for (var i = 0; i < 5; i++)
            await s.StartRunAsync();

        Assert.Equal(2, (await s.ClaimAsync(host, freeSlots: 2)).Tasks.Count);
    }

    [Fact]
    public async Task Group_task_limit_caps_leased_tasks_across_hosts()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), maxConcurrentTasks: 3);
        var first = await s.AddHostAsync();
        var second = await s.AddHostAsync();
        for (var i = 0; i < 6; i++)
            await s.StartRunAsync();

        var leased = (await s.ClaimAsync(first)).Tasks.Count + (await s.ClaimAsync(second)).Tasks.Count;

        Assert.Equal(3, leased);
        Assert.Equal(3, await s.LimitInUseAsync());
    }

    [Fact]
    public async Task Finishing_a_task_frees_its_group_slot()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"), maxConcurrentTasks: 1);
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        await s.StartRunAsync();
        var first = await s.ClaimOneAsync(host);
        Assert.Empty((await s.ClaimAsync(host)).Tasks);

        await s.CompleteAsync(first);

        Assert.Single((await s.ClaimAsync(host)).Tasks);
    }

    [Fact]
    public async Task Higher_effective_priority_is_dispatched_first()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var low = await s.StartRunAsync(priority: 40);
        var high = await s.StartRunAsync(priority: 70);

        Assert.Equal(high, (await s.ClaimOneAsync(host)).RunId);
        Assert.Equal(low, (await s.ClaimOneAsync(host)).RunId);
    }

    [Fact]
    public async Task Equal_priority_is_dispatched_oldest_first()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var older = await s.StartRunAsync();
        var newer = await s.StartRunAsync();

        Assert.Equal(older, (await s.ClaimOneAsync(host)).RunId);
        Assert.Equal(newer, (await s.ClaimOneAsync(host)).RunId);
    }

    [Fact]
    public async Task Base_priority_is_clamped_to_100()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a").PriorityOffset(10));

        var runId = await s.StartRunAsync(priority: 95);

        Assert.Equal(100, (await s.TaskAsync(runId, "a")).Priority);
    }

    [Fact]
    public async Task Every_claim_issues_a_greater_token()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync();
        var runId = await s.StartRunAsync();
        var first = await s.ClaimOneAsync(host);
        await s.ExpireLeaseAsync(first.TaskRunId);
        await s.Reaper.ReapAsync(CancellationToken.None);
        await HostData.ExecuteAsync(pg, "UPDATE task_runs SET not_before = now() WHERE id = @id", ("id", first.TaskRunId));
        await s.Promoter.PromoteAsync(CancellationToken.None);

        var second = await s.ClaimOneAsync(host);

        Assert.Equal(2, second.Attempt);
        Assert.True(second.LeaseToken > first.LeaseToken);
        Assert.Equal(runId, second.RunId);
    }

    [Fact]
    public async Task Lease_carries_the_outputs_of_every_succeeded_ancestor()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf =>
        {
            var a = wf.Task<Noop>("a");
            var b = wf.Task<Noop>("b").DependsOn(a);
            wf.Task<Noop>("c").DependsOn(b);
        });
        var host = await s.AddHostAsync();
        await s.StartRunAsync();
        await s.CompleteAsync(await s.ClaimOneAsync(host), output: """{"rows":42}""");
        await s.CompleteAsync(await s.ClaimOneAsync(host), output: "\"done\"");

        var c = await s.ClaimOneAsync(host);

        Assert.Equal("c", c.TaskKey);
        Assert.Equal("""{"rows": 42}""", c.UpstreamOutputs["a"]);
        Assert.Equal("\"done\"", c.UpstreamOutputs["b"]);
    }
}
