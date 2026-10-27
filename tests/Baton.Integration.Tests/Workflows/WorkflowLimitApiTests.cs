using System.Net;
using System.Net.Http.Json;
using Baton.ControlPlane.Api.Security;
using Baton.ControlPlane.Api.Workflows;
using Baton.Domain.Automations;
using Baton.Integration.Tests.Infrastructure;

namespace Baton.Integration.Tests.Workflows;

/// <summary>ORC-02 "Workflow run limit": Admins set it per workflow key (M4 task 1.5, design.md decision 4).</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class WorkflowLimitApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private ControlPlaneHost _api = null!;
    private HttpClient _admin = null!;
    private string _key = null!;

    public async Task InitializeAsync()
    {
        _api = await ControlPlaneHost.StartAsync(pg);
        _admin = _api.Client(BatonRoles.Admin);
        _key = DynamicWorkflow.NewKey("limited");
        await DispatchScenario.RegisterAsync(pg, new DynamicWorkflow(_key));
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task Admin_sets_changes_and_removes_a_workflow_limit()
    {
        using var viewer = _api.Client(BatonRoles.Viewer);

        var unlimited = await viewer.GetFromJsonAsync<WorkflowLimitResponse>($"/api/workflows/{_key}/limit");
        var set = await _admin.PutAsJsonAsync($"/api/workflows/{_key}/limit", new WorkflowLimitRequest(2));
        var changed = await _admin.PutAsJsonAsync($"/api/workflows/{_key}/limit", new WorkflowLimitRequest(3));
        var read = await viewer.GetFromJsonAsync<WorkflowLimitResponse>($"/api/workflows/{_key}/limit");
        var removed = await _admin.DeleteAsync($"/api/workflows/{_key}/limit");

        Assert.Equal(new WorkflowLimitResponse(_key, null, 0), unlimited);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(new WorkflowLimitResponse(_key, 3, 0), read);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM concurrency_limits WHERE key = @key",
            ("key", AutomationRules.WorkflowLimitKey(_key))));
    }

    [Fact]
    public async Task Only_admins_change_limits()
    {
        using var operatorClient = _api.Client(BatonRoles.Operator);

        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PutAsJsonAsync($"/api/workflows/{_key}/limit", new WorkflowLimitRequest(2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.DeleteAsync($"/api/workflows/{_key}/limit")).StatusCode);
    }

    [Fact]
    public async Task Unknown_workflow_and_invalid_limit_are_rejected()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsJsonAsync("/api/workflows/no-such-workflow/limit", new WorkflowLimitRequest(2))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync($"/api/workflows/{_key}/limit", new WorkflowLimitRequest(0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync($"/api/workflows/{_key}/limit", new WorkflowLimitRequest(null))).StatusCode);
    }
}
