using Baton.Abstractions;
using Baton.Domain.Tests.Samples;
using Baton.Domain.Workflows;
using Baton.Integration.Tests.Infrastructure;
using Baton.Persistence.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace Baton.Integration.Tests.Workflows;

/// <summary>WF-01, WF-04: startup registration into <c>workflow_definitions</c>.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class WorkflowRegistrationTests(PostgresFixture pg)
{
    private readonly WorkflowRegistrar _registrar = new(pg.DataSource);

    [Fact]
    public async Task New_definition_is_inserted_with_graph_hash_and_schema()
    {
        var workflow = new DynamicWorkflow(DynamicWorkflow.NewKey("insert")).Compile();

        var summary = await _registrar.RegisterAsync([workflow], CancellationToken.None);

        Assert.Equal(new RegistrationSummary(Inserted: 1, Unchanged: 0), summary);
        var row = Assert.Single(await RowsAsync(workflow.Key));
        Assert.Equal(1, row.Version);
        Assert.Equal(workflow.Hash, row.Hash);
        Assert.True(await JsonbEqualAsync(row.Graph, workflow.CanonicalJson));
        Assert.True(await JsonbEqualAsync(row.ParamsSchema, workflow.ParamsSchema));
        Assert.Equal(workflow.DisplayName, row.DisplayName);
    }

    [Fact]
    public async Task Re_registering_an_unchanged_definition_is_a_no_op()
    {
        var key = DynamicWorkflow.NewKey("noop");
        await _registrar.RegisterAsync([new DynamicWorkflow(key).Compile()], CancellationToken.None);

        var summary = await _registrar.RegisterAsync([new DynamicWorkflow(key).Compile()], CancellationToken.None);

        Assert.Equal(new RegistrationSummary(Inserted: 0, Unchanged: 1), summary);
        Assert.Single(await RowsAsync(key));
    }

    [Fact]
    public async Task Changed_graph_under_the_same_version_fails_and_writes_nothing()
    {
        var key = DynamicWorkflow.NewKey("changed");
        var original = new DynamicWorkflow(key, variant: 0).Compile();
        await _registrar.RegisterAsync([original], CancellationToken.None);
        var unrelated = new DynamicWorkflow(DynamicWorkflow.NewKey("unrelated")).Compile();

        var ex = await Assert.ThrowsAsync<WorkflowDefinitionException>(() => _registrar.RegisterAsync(
            [unrelated, new DynamicWorkflow(key, variant: 1).Compile()], CancellationToken.None));

        Assert.Equal([$"{key}@1: already registered with a different graph; bump Version to change it (ADR-0004)"], ex.Errors);
        Assert.Equal(original.Hash, Assert.Single(await RowsAsync(key)).Hash);
        Assert.Empty(await RowsAsync(unrelated.Key)); // same transaction, rolled back
    }

    [Fact]
    public async Task New_display_name_is_refreshed_without_a_version_bump()
    {
        var key = DynamicWorkflow.NewKey("label");
        await _registrar.RegisterAsync([new DynamicWorkflow(key).Compile()], CancellationToken.None);

        var summary = await _registrar.RegisterAsync(
            [new DynamicWorkflow(key, displayName: "Renamed workflow").Compile()], CancellationToken.None);

        Assert.Equal(new RegistrationSummary(Inserted: 0, Unchanged: 1), summary);
        Assert.Equal("Renamed workflow", Assert.Single(await RowsAsync(key)).DisplayName);
    }

    [Fact]
    public async Task Old_versions_remain_when_a_new_version_is_registered()
    {
        var key = DynamicWorkflow.NewKey("versions");
        var v1 = new DynamicWorkflow(key, version: 1).Compile();
        await _registrar.RegisterAsync([v1], CancellationToken.None);

        await _registrar.RegisterAsync([new DynamicWorkflow(key, version: 2).Compile()], CancellationToken.None);

        var rows = await RowsAsync(key);
        Assert.Equal([1, 2], rows.Select(r => r.Version));
        Assert.Equal(v1.Hash, rows[0].Hash);
    }

    [Fact]
    public async Task Replicas_registering_at_the_same_time_insert_each_definition_once()
    {
        var keys = Enumerable.Range(0, 3).Select(_ => DynamicWorkflow.NewKey("race")).ToList();
        var workflows = keys.Select(k => new DynamicWorkflow(k).Compile()).ToList();

        var summaries = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _registrar.RegisterAsync(workflows, CancellationToken.None))));

        Assert.Equal(keys.Count, summaries.Sum(s => s.Inserted));
        Assert.All(summaries, s => Assert.Equal(keys.Count, s.Inserted + s.Unchanged));
        foreach (var key in keys)
            Assert.Single(await RowsAsync(key));
    }

    [Fact]
    public async Task Definitions_package_is_discovered_registered_and_includes_the_spec_example()
    {
        await using var provider = new ServiceCollection()
            .AddWorkflows(typeof(Housekeeping).Assembly)
            .BuildServiceProvider();
        var set = provider.GetRequiredService<WorkflowSet>();

        await _registrar.RegisterAsync(set.Workflows, CancellationToken.None);

        Assert.Equal(["housekeeping@1", "nightly-sales-report@2"], set.Workflows.Select(w => w.Id));
        Assert.Contains(await RowsAsync("housekeeping"), r => r.Version == 1);
        var report = Assert.Single(await RowsAsync("nightly-sales-report"));
        Assert.Equal(set.Workflows.Single(w => w.Key == "nightly-sales-report").Hash, report.Hash);
        Assert.IsType<NightlySalesReport>(set.Workflows[1].Definition);
        Assert.Equal(typeof(SalesReportParams), ((IWorkflowDefinition)set.Workflows[1].Definition).ParamsType);
    }

    private async Task<List<Row>> RowsAsync(string key)
    {
        await using var cmd = pg.DataSource.CreateCommand("""
            SELECT version, graph_hash, display_name, graph::text, params_schema::text
            FROM workflow_definitions WHERE key = @key ORDER BY version
            """);
        cmd.Parameters.AddWithValue("key", key);

        var rows = new List<Row>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new Row(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return rows;
    }

    /// <summary>jsonb normalizes key order and spacing, so compare as jsonb rather than as text.</summary>
    private async Task<bool> JsonbEqualAsync(string stored, string expected)
    {
        await using var cmd = pg.DataSource.CreateCommand("SELECT @stored::jsonb = @expected::jsonb");
        cmd.Parameters.AddWithValue("stored", stored);
        cmd.Parameters.AddWithValue("expected", expected);
        return await cmd.ExecuteScalarAsync() is true;
    }

    private sealed record Row(int Version, string Hash, string DisplayName, string Graph, string ParamsSchema);
}
