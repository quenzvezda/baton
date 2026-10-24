using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Baton.ControlPlane.Api.Workflows;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence;
using Baton.Persistence.Definitions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Baton.Integration.Tests.Workflows;

/// <summary>WF-09: catalog API over a real Kestrel host on a free port and the shared Postgres.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CatalogApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly HttpClient Http = new();

    private WebApplication _app = null!;
    private Uri _baseAddress = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddBatonPersistence(pg.ConnectionString);

        _app = builder.Build();
        _app.MapWorkflowCatalog();
        await _app.StartAsync();

        _baseAddress = new Uri(_app.Urls.First());
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

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

    private Uri Url(string path) => new(_baseAddress, path);

    private async Task<string> RegisterVersionsAsync(string prefix, params int[] versions)
    {
        var key = DynamicWorkflow.NewKey(prefix);
        var registrar = new WorkflowRegistrar(pg.DataSource);
        await registrar.RegisterAsync([.. versions.Select(v => new DynamicWorkflow(key, v).Compile())], CancellationToken.None);
        return key;
    }
}
