using Baton.Domain.Automations;
using Baton.Integration.Tests.Infrastructure;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Definitions;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Runs;

/// <summary>ORC-01 (one run per firing), AUT-02 (workflow version), run creation details.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RunCreationTests(PostgresFixture pg)
{
    [Fact]
    public async Task The_same_firing_from_two_replicas_creates_one_run()
    {
        var s = await RunScenario.CreateAsync(pg, policy: "allow", maxRuns: 10);
        var key = $"automation:{s.AutomationId}:trigger:{Guid.NewGuid()}:2026-10-27T02:00:00.0000000Z";

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => s.FireAsync(key: key))));

        Assert.Single(results, r => r.Outcome == FireOutcome.Created);
        Assert.All(results, r => Assert.Equal(results.Single(x => x.Outcome == FireOutcome.Created).RunId, r.RunId));
        Assert.Equal(1L, await HostData.ScalarAsync<long>(pg, "SELECT count(*) FROM workflow_runs WHERE idempotency_key = @key", ("key", key)));
    }

    [Fact]
    public async Task A_run_records_what_admission_needs()
    {
        var s = await RunScenario.CreateAsync(pg);
        var automation = await RunScenario.InsertAutomationAsync(pg, s.GroupId, s.WorkflowKey, maxRuns: 2, priority: 70,
            runTimeout: TimeSpan.FromHours(2), labels: ["region-apac"]);

        var runId = await s.CreateRunAsync(automation);

        await using var cmd = pg.DataSource.CreateCommand("""
            SELECT status, priority, limit_keys, required_labels, run_timeout, trigger_kind FROM workflow_runs WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", runId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal("pending", r.GetString(0));
        Assert.Equal(70, r.GetInt16(1));
        Assert.Equal(
            new[] { AutomationRules.AutomationLimitKey(automation), s.GroupRunsLimit, s.WorkflowLimit }.Order(StringComparer.Ordinal),
            r.GetFieldValue<string[]>(2));
        Assert.Equal(["region-apac"], r.GetFieldValue<string[]>(3));
        Assert.Equal(TimeSpan.FromHours(2), r.GetFieldValue<TimeSpan>(4));
        Assert.Equal("manual", r.GetString(5));
    }

    [Fact]
    public async Task Following_latest_uses_the_version_registered_when_the_run_is_created()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var key = DynamicWorkflow.NewKey("latest");
        await RegisterAsync(new DynamicWorkflow(key, 2));
        var automation = await RunScenario.InsertAutomationAsync(pg, groupId, key, policy: "allow");
        var creator = new RunCreator(pg.DataSource);

        var before = await FireAsync(creator, automation);
        await RegisterAsync(new DynamicWorkflow(key, 3));
        var after = await FireAsync(creator, automation);

        Assert.Equal(2, await VersionOfAsync(before));
        Assert.Equal(3, await VersionOfAsync(after));
    }

    [Fact]
    public async Task A_pinned_version_keeps_being_used_after_a_newer_one_is_registered()
    {
        var (groupId, _) = await HostData.CreateGroupAsync(pg);
        var key = DynamicWorkflow.NewKey("pinned");
        await RegisterAsync(new DynamicWorkflow(key, 2));
        await RegisterAsync(new DynamicWorkflow(key, 3));
        var automation = await RunScenario.InsertAutomationAsync(pg, groupId, key, policy: "allow", workflowVersion: 2);

        var runId = await FireAsync(new RunCreator(pg.DataSource), automation);

        Assert.Equal(2, await VersionOfAsync(runId));
    }

    [Fact]
    public async Task Disabled_and_deleted_automations_create_no_run()
    {
        var s = await RunScenario.CreateAsync(pg);
        var disabled = await s.AddAutomationAsync();
        var deleted = await s.AddAutomationAsync();
        await HostData.ExecuteAsync(pg, "UPDATE automations SET enabled = false WHERE id = @id", ("id", disabled));
        await HostData.ExecuteAsync(pg, "UPDATE automations SET deleted_at = now() WHERE id = @id", ("id", deleted));

        Assert.Equal(FireOutcome.AutomationDisabled, (await s.FireAsync(disabled)).Outcome);
        Assert.Equal(FireOutcome.AutomationNotFound, (await s.FireAsync(deleted)).Outcome);
    }

    private async Task RegisterAsync(DynamicWorkflow workflow) =>
        await new WorkflowRegistrar(pg.DataSource).RegisterAsync([workflow.Compile()], CancellationToken.None);

    private static async Task<Guid> FireAsync(RunCreator creator, Guid automation)
    {
        var result = await creator.FireAsync(new FireRequest(automation, null, "manual", Guid.NewGuid().ToString()), CancellationToken.None);
        Assert.Equal(FireOutcome.Created, result.Outcome);
        return result.RunId!.Value;
    }

    private Task<int> VersionOfAsync(Guid runId) => HostData.ScalarAsync<int>(pg,
        "SELECT d.version FROM workflow_runs r JOIN workflow_definitions d ON d.id = r.definition_id WHERE r.id = @id", ("id", runId));
}
