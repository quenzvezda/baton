using Baton.Abstractions;
using Baton.Domain.Automations;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Queue;
using NpgsqlTypes;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>
/// A <see cref="DispatchScenario"/> plus automations of its workflow, seeded straight into the database, and the
/// run-lifecycle SQL (creation, admission, cancellation): what an M4 lifecycle test needs.
/// </summary>
internal sealed class RunScenario
{
    private RunScenario(DispatchScenario dispatch, string workflowKey, Guid automationId)
    {
        Dispatch = dispatch;
        WorkflowKey = workflowKey;
        AutomationId = automationId;
        Creator = new RunCreator(dispatch.Pg.DataSource);
        Admission = new RunAdmission(dispatch.Pg.DataSource, dispatch.Runs);
        Cancellation = new RunCancellation(dispatch.Pg.DataSource);
    }

    public DispatchScenario Dispatch { get; }
    public PostgresFixture Pg => Dispatch.Pg;
    public Guid GroupId => Dispatch.GroupId;
    public string WorkflowKey { get; }

    /// <summary>The scenario's first automation.</summary>
    public Guid AutomationId { get; }

    public RunCreator Creator { get; }
    public RunAdmission Admission { get; }
    public RunCancellation Cancellation { get; }

    /// <param name="groupMaxRuns">Creates the group's <c>group-runs</c> limit row.</param>
    /// <param name="workflowMaxRuns">Creates the workflow's <c>workflow:{key}</c> limit row.</param>
    public static async Task<RunScenario> CreateAsync(
        PostgresFixture pg, Action<IWorkflowBuilder<NoParams>>? define = null, string policy = "skip", int maxRuns = 1,
        int? groupMaxRuns = null, int? workflowMaxRuns = null, int priority = 50, TimeSpan? runTimeout = null)
    {
        var dispatch = await DispatchScenario.CreateAsync(pg, define ?? (wf => wf.Task<Noop>("a")));
        var key = await HostData.ScalarAsync<string>(pg, "SELECT key FROM workflow_definitions WHERE id = @id", ("id", dispatch.DefinitionId));
        if (groupMaxRuns is { } groupMax)
            await SetLimitAsync(pg, DbNames.GroupRunsLimit(dispatch.GroupId), groupMax);
        if (workflowMaxRuns is { } workflowMax)
            await SetLimitAsync(pg, AutomationRules.WorkflowLimitKey(key), workflowMax);

        var automationId = await InsertAutomationAsync(pg, dispatch.GroupId, key, policy, maxRuns, priority, runTimeout);
        return new RunScenario(dispatch, key, automationId);
    }

    /// <summary>Another automation of the same workflow and group.</summary>
    public Task<Guid> AddAutomationAsync(string policy = "skip", int maxRuns = 1, int priority = 50, TimeSpan? runTimeout = null) =>
        InsertAutomationAsync(Pg, GroupId, WorkflowKey, policy, maxRuns, priority, runTimeout);

    public static async Task<Guid> InsertAutomationAsync(
        PostgresFixture pg, Guid groupId, string workflowKey, string policy = "skip", int maxRuns = 1, int priority = 50,
        TimeSpan? runTimeout = null, int? workflowVersion = null, string[]? labels = null)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = pg.DataSource.CreateCommand("""
            INSERT INTO automations (id, name, workflow_key, workflow_version, host_group_id, required_labels, priority,
                                     max_concurrent_runs, concurrency_policy, run_timeout, owner)
            VALUES (@id, @name, @key, @version, @group, @labels, @priority, @max, @policy, @timeout, 'tests')
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", HostData.NewName("auto"));
        cmd.Parameters.AddWithValue("key", workflowKey);
        cmd.Parameters.AddWithValue("version", NpgsqlDbType.Integer, (object?)workflowVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("group", groupId);
        cmd.Parameters.AddWithValue("labels", labels ?? []);
        cmd.Parameters.AddWithValue("priority", (short)priority);
        cmd.Parameters.AddWithValue("max", maxRuns);
        cmd.Parameters.AddWithValue("policy", policy);
        cmd.Parameters.AddWithValue("timeout", NpgsqlDbType.Interval, (object?)runTimeout ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        await SetLimitAsync(pg, AutomationRules.AutomationLimitKey(id), maxRuns);
        return id;
    }

    public static Task SetLimitAsync(PostgresFixture pg, string key, int max) =>
        HostData.ExecuteAsync(pg, "INSERT INTO concurrency_limits (key, max) VALUES (@key, @max) ON CONFLICT (key) DO UPDATE SET max = @max",
            ("key", key), ("max", max));

    /// <summary>A Run now firing of <paramref name="automationId"/> (default: the scenario's automation).</summary>
    public Task<FireResult> FireAsync(Guid? automationId = null, string? key = null) =>
        Creator.FireAsync(new FireRequest(automationId ?? AutomationId, null, AutomationRules.ManualKind, key ?? Guid.NewGuid().ToString()),
            CancellationToken.None);

    /// <summary>Fires and fails the test unless a run was created.</summary>
    public async Task<Guid> CreateRunAsync(Guid? automationId = null)
    {
        var result = await FireAsync(automationId);
        Assert.Equal(FireOutcome.Created, result.Outcome);
        return result.RunId!.Value;
    }

    /// <summary>Creates a run and admits it; fails the test unless it starts.</summary>
    public async Task<Guid> StartRunAsync(Guid? automationId = null)
    {
        var runId = await CreateRunAsync(automationId);
        await AdmitAsync();
        Assert.Equal("running", await RunStatusAsync(runId));
        return runId;
    }

    public Task<(int Admitted, int PassedOver, bool Busy)> AdmitAsync() => Admission.AdmitGroupAsync(GroupId, CancellationToken.None);

    public Task<string> RunStatusAsync(Guid runId) => Dispatch.RunStatusAsync(runId);

    public Task<int> InUseAsync(string key) =>
        HostData.ScalarAsync<int>(Pg, "SELECT in_use FROM concurrency_limits WHERE key = @key", ("key", key));

    public string AutomationLimit => AutomationRules.AutomationLimitKey(AutomationId);

    public string WorkflowLimit => AutomationRules.WorkflowLimitKey(WorkflowKey);

    public string GroupRunsLimit => DbNames.GroupRunsLimit(GroupId);

    /// <summary>Claims and succeeds every task of the run that can run, until the run is no longer Running.</summary>
    public async Task CompleteRunAsync(Guid runId, Guid hostId)
    {
        for (var i = 0; i < 50 && await RunStatusAsync(runId) is "running" or "cancelling"; i++)
        {
            foreach (var task in (await Dispatch.ClaimAsync(hostId, 4)).Tasks)
                Assert.True(await Dispatch.CompleteAsync(task));
        }
    }
}
