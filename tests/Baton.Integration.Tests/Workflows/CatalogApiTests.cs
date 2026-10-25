using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Security;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Definitions;

namespace Baton.Integration.Tests.Workflows;

/// <summary>WF-09: catalog API over a real Kestrel host on a free port and the shared Postgres.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CatalogApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private ControlPlaneHost _api = null!;
    private HttpClient Http { get; set; } = null!;

    public async Task InitializeAsync()
    {
        _api = await ControlPlaneHost.StartAsync(pg);
        Http = _api.Client(BatonRoles.Viewer);
    }

    public async Task DisposeAsync()
    {
        Http.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task List_shows_the_latest_version_of_each_workflow()
    {
        var key = await RegisterVersionsAsync("listed", 1, 2);

        var list = await Http.GetFromJsonAsync<JsonElement>(Url("/api/workflows"));

        var entry = list.EnumerateArray().Single(w => w.GetProperty("key").GetString() == key);
        Assert.Equal(2, entry.GetProperty("latestVersion").GetInt32());
        Assert.Equal(new DynamicWorkflow(key).DisplayName, entry.GetProperty("displayName").GetString());
        Assert.False(entry.GetProperty("isRetired").GetBoolean());
    }

    [Fact]
    public async Task Version_detail_has_the_graph_schema_and_version_history()
    {
        var key = await RegisterVersionsAsync("detail", 1, 2);
        var v1 = new DynamicWorkflow(key, version: 1).Compile();

        var detail = await Http.GetFromJsonAsync<JsonElement>(Url($"/api/workflows/{key}/versions/1"));

        Assert.Equal(key, detail.GetProperty("key").GetString());
        Assert.Equal(1, detail.GetProperty("version").GetInt32());
        Assert.Equal(v1.Hash, detail.GetProperty("graphHash").GetString());
        Assert.Equal(["first"], detail.GetProperty("graph").GetProperty("tasks").EnumerateArray()
            .Select(t => t.GetProperty("key").GetString()));
        Assert.Equal("object", detail.GetProperty("paramsSchema").GetProperty("type").GetString());
        Assert.Equal([1, 2], detail.GetProperty("versions").EnumerateArray().Select(v => v.GetInt32()));
    }

    [Fact]
    public async Task Unknown_version_is_not_found()
    {
        var key = await RegisterVersionsAsync("missing", 1, 2);

        var response = await Http.GetAsync(Url($"/api/workflows/{key}/versions/9"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_key_is_not_found()
    {
        var response = await Http.GetAsync(Url("/api/workflows/no-such-workflow/versions/1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private async Task<string> RegisterVersionsAsync(string prefix, params int[] versions)
    {
        var key = DynamicWorkflow.NewKey(prefix);
        var registrar = new WorkflowRegistrar(pg.DataSource);
        await registrar.RegisterAsync([.. versions.Select(v => new DynamicWorkflow(key, v).Compile())], CancellationToken.None);
        return key;
    }
}
