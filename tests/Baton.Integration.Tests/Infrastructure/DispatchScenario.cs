using Baton.Abstractions;
using Baton.Domain.Runs;
using Baton.Domain.Workflows;
using Baton.Integration.Tests.Workflows;
using Baton.Persistence.Definitions;
using Baton.Persistence.Queue;

namespace Baton.Integration.Tests.Infrastructure;

/// <summary>A workflow whose graph the test declares, registered under a fresh key.</summary>
internal sealed class GraphWorkflow(Action<IWorkflowBuilder<NoParams>> define) : WorkflowDefinition<NoParams>
{
    public override string Key { get; } = DynamicWorkflow.NewKey("wf");
    public override int Version => 1;

    protected override void Define(IWorkflowBuilder<NoParams> wf) => define(wf);
}

/// <summary>The state of one task run, read straight from <c>task_runs</c>.</summary>
internal sealed record TaskRow(
    Guid Id, string Status, int Attempt, long? LeaseToken, Guid? LeaseHostId, DateTime? LeaseExpiresAt, DateTime? NotBefore,
    int UpstreamRemaining, string? Output, string? Error, short Priority);

/// <summary>
/// One host group with a registered workflow, plus hosts and runs in it: what a dispatch test needs.
/// Runs start through <see cref="RunMaterializer"/>, as M4 admission will (task 3.4).
/// </summary>
internal sealed class DispatchScenario
{
    private DispatchScenario(PostgresFixture pg, Guid groupId, string groupName, long definitionId, string hash)
    {
        Pg = pg;
        GroupId = groupId;
        GroupName = groupName;
        DefinitionId = definitionId;
        Hash = hash;
        Graphs = new DefinitionGraphCache(pg.DataSource);
        Claims = new TaskClaims(pg.DataSource, Graphs);
        Leases = new TaskLeases(pg.DataSource, Graphs);
        Runs = new RunMaterializer(pg.DataSource, Graphs);
        Reaper = new LeaseReaper(pg.DataSource, Graphs);
        Promoter = new RetryPromoter(pg.DataSource);
    }

    public PostgresFixture Pg { get; }
    public Guid GroupId { get; }
    public string GroupName { get; }
    public long DefinitionId { get; }
    public string Hash { get; }
    public DefinitionGraphCache Graphs { get; }
    public TaskClaims Claims { get; }
    public TaskLeases Leases { get; }
    public RunMaterializer Runs { get; }
    public LeaseReaper Reaper { get; }
    public RetryPromoter Promoter { get; }

    /// <param name="maxConcurrentTasks">Creates the group's <c>group-tasks</c> limit row (HST-01).</param>
    public static async Task<DispatchScenario> CreateAsync(
        PostgresFixture pg, Action<IWorkflowBuilder<NoParams>> define, int? maxConcurrentTasks = null, int leaseSeconds = 60)
    {
        var (groupId, groupName) = await HostData.CreateGroupAsync(pg);
        await HostData.ExecuteAsync(pg, "UPDATE host_groups SET lease_seconds = @lease WHERE id = @id", ("lease", leaseSeconds), ("id", groupId));
        if (maxConcurrentTasks is { } max)
        {
            await HostData.ExecuteAsync(pg, "UPDATE host_groups SET max_concurrent_tasks = @max WHERE id = @id", ("max", max), ("id", groupId));
            await HostData.ExecuteAsync(pg, "INSERT INTO concurrency_limits (key, max) VALUES (@key, @max)",
                ("key", DbNames.GroupTasksLimit(groupId)), ("max", max));
        }

        var (definitionId, hash) = await RegisterAsync(pg, new GraphWorkflow(define));
        return new DispatchScenario(pg, groupId, groupName, definitionId, hash);
    }

    public static async Task<(long Id, string Hash)> RegisterAsync(PostgresFixture pg, IWorkflowDefinition definition)
    {
        var compiled = WorkflowCompiler.Compile(definition);
        await new WorkflowRegistrar(pg.DataSource).RegisterAsync([compiled], CancellationToken.None);
        var id = await HostData.ScalarAsync<long>(pg, "SELECT id FROM workflow_definitions WHERE graph_hash = @hash", ("hash", compiled.Hash));
        return (id, compiled.Hash);
    }

    /// <summary>An Online host of the group that carries the scenario's definition unless told otherwise.</summary>
    public Task<Guid> AddHostAsync(int slots = 4, string[]? labels = null, bool carriesDefinition = true) =>
        HostData.InsertHostAsync(Pg, GroupId, slots: slots, labels: labels, hashes: carriesDefinition ? [Hash] : []);

    public Task<Guid> StartRunAsync(int priority = 50, string[]? labels = null, string parameters = "{}") =>
        Runs.StartAsync(new NewRun(DefinitionId, GroupId, priority, parameters, labels ?? [], "manual", Guid.NewGuid().ToString()),
            CancellationToken.None);

    public async Task<ClaimResult> ClaimAsync(Guid hostId, int freeSlots = 4) =>
        await Claims.ClaimAsync(GroupId, hostId, freeSlots, CancellationToken.None)
        ?? throw new InvalidOperationException("Host is unknown to the scenario's group.");

    /// <summary>Claims exactly one task and fails the test otherwise.</summary>
    public async Task<ClaimedTask> ClaimOneAsync(Guid hostId) => Assert.Single((await ClaimAsync(hostId, 1)).Tasks);

    public Task<bool> CompleteAsync(ClaimedTask task, AttemptOutcome outcome = AttemptOutcome.Succeeded, string? output = "1",
        bool retryable = true) =>
        Leases.CompleteAsync(GroupId, Report(task.TaskRunId, task.LeaseToken, outcome, output, retryable), CancellationToken.None);

    public static AttemptReport Report(Guid taskRunId, long token, AttemptOutcome outcome, string? output = "1", bool retryable = true) =>
        new(taskRunId, token, outcome, outcome == AttemptOutcome.Succeeded ? output : null, output?.Length ?? 0,
            outcome == AttemptOutcome.Succeeded ? null : TaskErrors.Json("TestError", "boom"), retryable);

    public async Task<TaskRow> TaskAsync(Guid runId, string key)
    {
        await using var cmd = Pg.DataSource.CreateCommand("""
            SELECT id, status, attempt, lease_token, lease_host_id, lease_expires_at, not_before, upstream_remaining,
                   output::text, error::text, priority
            FROM task_runs WHERE workflow_run_id = @runId AND task_key = @key
            """);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("key", key);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), $"No task '{key}' in run {runId}.");
        return new TaskRow(
            r.GetGuid(0), r.GetString(1), r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetGuid(4),
            r.IsDBNull(5) ? null : r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetDateTime(6),
            r.GetInt32(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt16(10));
    }

    public Task<string> RunStatusAsync(Guid runId) =>
        HostData.ScalarAsync<string>(Pg, "SELECT status FROM workflow_runs WHERE id = @id", ("id", runId));

    public Task<int> LimitInUseAsync() =>
        HostData.ScalarAsync<int>(Pg, "SELECT in_use FROM concurrency_limits WHERE key = @key", ("key", DbNames.GroupTasksLimit(GroupId)));

    /// <summary>Moves the lease's expiry into the past, as if the host had stopped heartbeating.</summary>
    public Task ExpireLeaseAsync(Guid taskRunId) =>
        HostData.ExecuteAsync(Pg, "UPDATE task_runs SET lease_expires_at = now() - interval '1 second' WHERE id = @id", ("id", taskRunId));

    public async Task<IReadOnlyList<string>> EventTypesAsync(Guid runId, string? taskKey = null)
    {
        var types = new List<string>();
        await using var cmd = Pg.DataSource.CreateCommand("""
            SELECT type FROM run_events WHERE workflow_run_id = @runId AND (@key::text IS NULL OR task_key = @key) ORDER BY id
            """);
        cmd.Parameters.AddWithValue("runId", runId);
        cmd.Parameters.AddWithValue("key", (object?)taskKey ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            types.Add(reader.GetString(0));
        return types;
    }

    public static TaskRunStatus Status(TaskRow row) => DbNames.TaskStatus(row.Status);
}
