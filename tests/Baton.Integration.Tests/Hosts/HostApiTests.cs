using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Hosts;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Hosts;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Definitions;

namespace Baton.Integration.Tests.Hosts;

/// <summary>HST-04..06: inventory, drain and undrain, group move and removal through the API.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class HostApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private ControlPlaneHost _api = null!;

    public async Task InitializeAsync() => _api = await ControlPlaneHost.StartAsync(pg);

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Inventory_reflects_running_work()
    {
        var (groupId, groupName) = await HostData.CreateGroupAsync(pg, "etl");
        var name = HostData.NewName("etl-01");
        var hostId = await HostData.InsertHostAsync(pg, groupId, name, slots: 4);
        var taskIds = await SeedTasksAsync(groupId, hostId, ("extract", "running"), ("load", "leased"), ("report", "running"), ("done", "succeeded"));
        using var http = _api.Client(BatonRoles.Viewer);

        var hosts = await http.GetFromJsonAsync<JsonElement>(Url("/api/hosts"));

        var host = hosts.EnumerateArray().Single(h => h.GetProperty("name").GetString() == name);
        Assert.Equal(HostStatus.Online, host.GetProperty("status").GetString());
        Assert.Equal(groupName, host.GetProperty("hostGroupName").GetString());
        Assert.Equal(4, host.GetProperty("slotsTotal").GetInt32());
        Assert.Equal(3, host.GetProperty("slotsUsed").GetInt32());
        Assert.Equal(["extract", "load", "report"], host.GetProperty("currentTasks").EnumerateArray()
            .Select(t => t.GetProperty("taskKey").GetString()));
        Assert.Equal(taskIds["extract"], host.GetProperty("currentTasks")[0].GetProperty("taskRunId").GetGuid());
        var lastHeartbeat = host.GetProperty("lastHeartbeatAt").GetDateTimeOffset();
        Assert.Equal(await HostData.ScalarAsync<DateTimeOffset>(pg, "SELECT last_heartbeat_at FROM hosts WHERE id = @id", ("id", hostId)), lastHeartbeat);
    }

    [Fact]
    public async Task Drain_keeps_running_tasks_and_undrain_returns_online()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var hostId = await HostData.InsertHostAsync(pg, groupId);
        await SeedTasksAsync(groupId, hostId, ("a", "running"), ("b", "running"));
        using var http = _api.Client(BatonRoles.Operator);

        var drain = await http.PostAsync(Url($"/api/hosts/{hostId}/drain"), null);
        var drained = await http.GetFromJsonAsync<JsonElement>(Url($"/api/hosts/{hostId}"));
        var eligibleWhileDrained = await HostData.IsEligibleAsync(pg, hostId);
        var undrain = await http.PostAsync(Url($"/api/hosts/{hostId}/undrain"), null);
        var undrained = await http.GetFromJsonAsync<JsonElement>(Url($"/api/hosts/{hostId}"));

        Assert.Equal(HttpStatusCode.NoContent, drain.StatusCode);
        Assert.Equal(HostStatus.Draining, drained.GetProperty("status").GetString());
        Assert.Equal(2, drained.GetProperty("slotsUsed").GetInt32());
        Assert.True(drained.GetProperty("drainRequested").GetBoolean());
        Assert.False(eligibleWhileDrained);
        Assert.Equal(HttpStatusCode.NoContent, undrain.StatusCode);
        Assert.Equal(HostStatus.Online, undrained.GetProperty("status").GetString());
        Assert.True(await HostData.IsEligibleAsync(pg, hostId));
    }

    [Fact]
    public async Task Drain_an_unknown_host_is_not_found()
    {
        using var http = _api.Client(BatonRoles.Operator);

        var response = await http.PostAsync(Url($"/api/hosts/{Guid.CreateVersion7()}/drain"), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_moves_a_host_and_held_tasks_are_unaffected()
    {
        var (etlId, _) = await HostData.CreateGroupAsync(pg, "etl");
        var (gpuId, gpuName) = await HostData.CreateGroupAsync(pg, "gpu");
        var hostId = await HostData.InsertHostAsync(pg, etlId);
        var tasks = await SeedTasksAsync(etlId, hostId, ("held", "running"));
        using var http = _api.Client(BatonRoles.Admin);

        var response = await http.PutAsJsonAsync(Url($"/api/hosts/{hostId}/group"), new MoveHostRequest(gpuId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var host = await http.GetFromJsonAsync<JsonElement>(Url($"/api/hosts/{hostId}"));
        Assert.Equal(gpuName, host.GetProperty("hostGroupName").GetString());
        Assert.Equal(1, host.GetProperty("slotsUsed").GetInt32());
        Assert.Equal(etlId, await HostData.ScalarAsync<Guid>(pg, "SELECT host_group_id FROM task_runs WHERE id = @id", ("id", tasks["held"])));
    }

    [Fact]
    public async Task Move_to_an_unknown_group_is_rejected()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var hostId = await HostData.InsertHostAsync(pg, groupId);
        using var http = _api.Client(BatonRoles.Admin);

        var response = await http.PutAsJsonAsync(Url($"/api/hosts/{hostId}/group"), new MoveHostRequest(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_move_a_host()
    {
        var (etlId, _) = await HostData.CreateGroupAsync(pg);
        var (gpuId, _) = await HostData.CreateGroupAsync(pg);
        var hostId = await HostData.InsertHostAsync(pg, etlId);
        using var http = _api.Client(BatonRoles.Operator);

        var response = await http.PutAsJsonAsync(Url($"/api/hosts/{hostId}/group"), new MoveHostRequest(gpuId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(etlId, await HostData.ScalarAsync<Guid>(pg, "SELECT host_group_id FROM hosts WHERE id = @id", ("id", hostId)));
    }

    [Fact]
    public async Task Remove_an_offline_host()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var hostId = await HostData.InsertHostAsync(pg, groupId);
        await HostData.AgeHeartbeatAsync(pg, hostId, TimeSpan.FromDays(1));
        using var http = _api.Client(BatonRoles.Admin);

        var response = await http.DeleteAsync(Url($"/api/hosts/{hostId}"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(Url($"/api/hosts/{hostId}"))).StatusCode);
    }

    [Fact]
    public async Task Remove_a_live_host_is_a_conflict()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var hostId = await HostData.InsertHostAsync(pg, groupId);
        using var http = _api.Client(BatonRoles.Admin);

        var response = await http.DeleteAsync(Url($"/api/hosts/{hostId}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Url($"/api/hosts/{hostId}"))).StatusCode);
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    /// <summary>One run of a registered workflow with a task per (key, status), leased to <paramref name="hostId"/>.</summary>
    private async Task<Dictionary<string, Guid>> SeedTasksAsync(Guid groupId, Guid hostId, params (string Key, string Status)[] tasks)
    {
        var workflow = new DynamicWorkflow(DynamicWorkflow.NewKey("inv")).Compile();
        await new WorkflowRegistrar(pg.DataSource).RegisterAsync([workflow], CancellationToken.None);
        var definitionId = await HostData.ScalarAsync<long>(pg,
            "SELECT id FROM workflow_definitions WHERE key = @key", ("key", workflow.Key));

        var runId = Guid.CreateVersion7();
        await HostData.ExecuteAsync(pg, """
            INSERT INTO workflow_runs (id, definition_id, status, priority, host_group_id, parameters, trigger_kind, idempotency_key)
            VALUES (@id, @definition, 'running', 50, @group, '{}', 'manual', @key)
            """, ("id", runId), ("definition", definitionId), ("group", groupId), ("key", runId.ToString()));

        var ids = new Dictionary<string, Guid>();
        foreach (var (key, status) in tasks)
        {
            var id = Guid.CreateVersion7();
            await HostData.ExecuteAsync(pg, """
                INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, attempt,
                                       max_attempts, timeout, priority, effective_priority, host_group_id, definition_hash,
                                       lease_host_id, lease_token, lease_expires_at)
                VALUES (@id, @run, @key, @status, 'all_success', 0, 1, 3, interval '1 hour', 50, 50, @group, @hash,
                        @host, nextval('lease_token_seq'), now() + interval '60 seconds')
                """, ("id", id), ("run", runId), ("key", key), ("status", status), ("group", groupId),
                ("hash", workflow.Hash), ("host", hostId));
            ids[key] = id;
        }
        return ids;
    }
}
