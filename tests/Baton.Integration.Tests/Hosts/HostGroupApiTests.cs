using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Hosts;
using Baton.ControlPlane.Api.Security;
using Baton.Domain.Hosts;
using Baton.Integration.Tests.Infrastructure;

namespace Baton.Integration.Tests.Hosts;

/// <summary>HST-01: host-group administration and its limit rows (ADR-0009).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class HostGroupApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private ControlPlaneHost _api = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _api = await ControlPlaneHost.StartAsync(pg);
        _admin = _api.Client(BatonRoles.Admin);
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Create_a_group_with_limits()
    {
        var name = HostData.NewName("etl");

        var response = await _admin.PostAsJsonAsync(Url("/api/host-groups"), new HostGroupRequest(name, 5, 20, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var group = await response.Content.ReadFromJsonAsync<HostGroupResponse>();
        Assert.NotNull(group);
        Assert.Equal(new HostGroupResponse(group.Id, name, 5, 20, 60, false), group);
        Assert.Equal($"/api/host-groups/{group.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(5, await LimitAsync(HostGroupRules.RunsLimitKey(group.Id)));
        Assert.Equal(20, await LimitAsync(HostGroupRules.TasksLimitKey(group.Id)));
    }

    [Fact]
    public async Task Absent_limit_means_unlimited_and_removing_a_limit_deletes_its_row()
    {
        var name = HostData.NewName("etl");
        var created = await (await _admin.PostAsJsonAsync(Url("/api/host-groups"), new HostGroupRequest(name, 5, null, null)))
            .Content.ReadFromJsonAsync<HostGroupResponse>();
        Assert.Null(await LimitAsync(HostGroupRules.TasksLimitKey(created!.Id)));

        var response = await _admin.PutAsJsonAsync(Url($"/api/host-groups/{created.Id}"), new HostGroupRequest(name, null, 8, 120));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await LimitAsync(HostGroupRules.RunsLimitKey(created.Id)));
        Assert.Equal(8, await LimitAsync(HostGroupRules.TasksLimitKey(created.Id)));
        var updated = await response.Content.ReadFromJsonAsync<HostGroupResponse>();
        Assert.Equal(120, updated!.LeaseSeconds);
    }

    [Fact]
    public async Task Duplicate_name_is_a_conflict()
    {
        var (_, name) = await HostData.CreateGroupAsync(pg);

        var response = await _admin.PostAsJsonAsync(Url("/api/host-groups"), new HostGroupRequest(name, null, null, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Renaming_onto_an_existing_name_is_a_conflict()
    {
        var (_, taken) = await HostData.CreateGroupAsync(pg);
        var (id, _) = await HostData.CreateGroupAsync(pg);

        var response = await _admin.PutAsJsonAsync(Url($"/api/host-groups/{id}"), new HostGroupRequest(taken, null, null, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData("Upper", null, null)]
    [InlineData("-leading-dash", null, null)]
    [InlineData("ok", 0, null)]
    [InlineData("ok", null, 10)]
    public async Task Invalid_settings_are_rejected(string name, int? maxRuns, int? leaseSeconds)
    {
        var response = await _admin.PostAsJsonAsync(Url("/api/host-groups"), new HostGroupRequest(name, maxRuns, null, leaseSeconds));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_a_group_with_hosts_is_rejected()
    {
        var (id, _) = await HostData.CreateGroupAsync(pg);
        await HostData.InsertHostAsync(pg, id);

        var response = await _admin.DeleteAsync(Url($"/api/host-groups/{id}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await GroupExistsAsync(id));
    }

    [Fact]
    public async Task Delete_a_group_with_automations_is_rejected()
    {
        var (id, _) = await HostData.CreateGroupAsync(pg);
        await HostData.ExecuteAsync(pg,
            "INSERT INTO automations (id, name, workflow_key, host_group_id, owner) VALUES (@id, @name, 'wf', @group, 'test')",
            ("id", Guid.CreateVersion7()), ("name", HostData.NewName("auto")), ("group", id));

        var response = await _admin.DeleteAsync(Url($"/api/host-groups/{id}"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await GroupExistsAsync(id));
    }

    [Fact]
    public async Task Delete_an_unused_group_removes_it_its_limits_and_credentials()
    {
        var name = HostData.NewName("tmp");
        var created = await (await _admin.PostAsJsonAsync(Url("/api/host-groups"), new HostGroupRequest(name, 2, 3, null)))
            .Content.ReadFromJsonAsync<HostGroupResponse>();
        await HostData.IssueCredentialAsync(pg, created!.Id);

        var response = await _admin.DeleteAsync(Url($"/api/host-groups/{created.Id}"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await GroupExistsAsync(created.Id));
        Assert.Null(await LimitAsync(HostGroupRules.RunsLimitKey(created.Id)));
        Assert.Null(await LimitAsync(HostGroupRules.TasksLimitKey(created.Id)));
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg,
            "SELECT count(*) FROM agent_credentials WHERE host_group_id = @id", ("id", created.Id)));
    }

    [Fact]
    public async Task Unknown_group_is_not_found()
    {
        var response = await _admin.DeleteAsync(Url($"/api/host-groups/{Guid.CreateVersion7()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_create_a_group()
    {
        using var http = _api.Client(BatonRoles.Operator);
        var name = HostData.NewName("etl");

        var response = await http.PostAsJsonAsync(Url("/api/host-groups"), new HostGroupRequest(name, null, null, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM host_groups WHERE name = @name", ("name", name)));
    }

    [Fact]
    public async Task Viewer_reads_groups_with_their_limits()
    {
        var name = HostData.NewName("etl");
        await _admin.PostAsJsonAsync(Url("/api/host-groups"), new HostGroupRequest(name, 5, 20, null));
        using var http = _api.Client(BatonRoles.Viewer);

        var groups = await http.GetFromJsonAsync<JsonElement>(Url("/api/host-groups"));

        var group = groups.EnumerateArray().Single(g => g.GetProperty("name").GetString() == name);
        Assert.Equal(5, group.GetProperty("maxConcurrentRuns").GetInt32());
        Assert.Equal(20, group.GetProperty("maxConcurrentTasks").GetInt32());
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<int?> LimitAsync(string key)
    {
        await using var cmd = pg.DataSource.CreateCommand("SELECT max FROM concurrency_limits WHERE key = @key");
        cmd.Parameters.AddWithValue("key", key);
        return await cmd.ExecuteScalarAsync() as int?;
    }

    private Task<bool> GroupExistsAsync(Guid id) =>
        HostData.ScalarAsync<bool>(pg, "SELECT EXISTS (SELECT 1 FROM host_groups WHERE id = @id)", ("id", id));
}
