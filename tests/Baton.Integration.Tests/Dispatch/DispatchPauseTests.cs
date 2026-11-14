using System.Diagnostics;
using Baton.Agent.Protocol.V1;
using Baton.ControlPlane.Api.Security;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Google.Protobuf.WellKnownTypes;

namespace Baton.Integration.Tests.Dispatch;

/// <summary>ORC-13: pausing a host group stops new leases only (M6 task 5.2).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class DispatchPauseTests(PostgresFixture pg)
{
    private Task PauseAsync(Guid groupId, bool paused = true) =>
        HostData.ExecuteAsync(pg, "UPDATE host_groups SET dispatch_paused = @p WHERE id = @id", ("p", paused), ("id", groupId));

    [Fact]
    public async Task Paused_group_leases_nothing_while_running_tasks_finish()
    {
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var host = await s.AddHostAsync(slots: 10);
        var running = new List<Baton.Persistence.Queue.ClaimedTask>();
        for (var i = 0; i < 3; i++)
        {
            await s.StartRunAsync();
            running.Add(await s.ClaimOneAsync(host));
        }

        var queued = new List<Guid>();
        for (var i = 0; i < 5; i++)
            queued.Add(await s.StartRunAsync());

        await PauseAsync(s.GroupId);

        Assert.Empty((await s.ClaimAsync(host, freeSlots: 10)).Tasks);
        foreach (var task in running)
            Assert.True(await s.CompleteAsync(task)); // spec "Pause stops new leases": the 3 run to completion
        foreach (var runId in queued)
            Assert.Equal("queued", (await s.TaskAsync(runId, "a")).Status);

        await PauseAsync(s.GroupId, paused: false);
        Assert.Equal(5, (await s.ClaimAsync(host, freeSlots: 10)).Tasks.Count);
    }

    [Fact]
    public async Task Runs_are_admitted_while_the_group_is_paused()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        await PauseAsync(s.GroupId);
        var runId = await s.CreateRunAsync();

        await s.AdmitAsync();

        Assert.Equal("running", await s.RunStatusAsync(runId));
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM task_runs WHERE workflow_run_id = @id AND upstream_remaining = 0 AND status <> 'queued'", ("id", runId)));
    }

    [Fact]
    public async Task Resume_wakes_a_long_polling_agent_within_2_s()
    {
        await using var orchestrator = await OrchestratorHost.StartAsync(pg, d => d.ClaimRecheck = TimeSpan.FromSeconds(30));
        await using var api = await ControlPlaneHost.StartAsync(pg);
        using var operatorClient = api.Client(BatonRoles.Operator);
        var s = await DispatchScenario.CreateAsync(pg, wf => wf.Task<Noop>("a"));
        var (_, token) = await HostData.IssueCredentialAsync(pg, s.GroupId);
        var hostId = (await orchestrator.RegisterAsync(token, HostData.NewName("host"), s.GroupName, hashes: [s.Hash])).HostId;
        await PauseAsync(s.GroupId);
        await s.StartRunAsync();
        await Task.Delay(500); // the listener has connected

        var claim = orchestrator.Client.ClaimAsync(
            new ClaimRequest { HostId = hostId, FreeSlots = 1, MaxWait = Duration.FromTimeSpan(TimeSpan.FromSeconds(20)) },
            OrchestratorHost.Bearer(token)).ResponseAsync;
        await Task.Delay(500);
        Assert.False(claim.IsCompleted); // paused: the claim keeps waiting

        var resumed = Stopwatch.StartNew();
        var response = await operatorClient.PostAsync(new Uri($"/api/host-groups/{s.GroupId}/resume", UriKind.Relative), null);
        response.EnsureSuccessStatusCode();
        var reply = await claim;

        Assert.Single(reply.Leases);
        Assert.True(resumed.Elapsed < TimeSpan.FromSeconds(2), $"Leased {resumed.Elapsed} after the resume");
    }
}
