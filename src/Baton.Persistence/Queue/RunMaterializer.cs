using Baton.Domain.Runs;
using Baton.Persistence.Definitions;
using Npgsql;
using NpgsqlTypes;

namespace Baton.Persistence.Queue;

/// <summary>A run to start right away, bypassing admission (until M4 admission exists, tests start runs this way).</summary>
public sealed record NewRun(
    long DefinitionId,
    Guid HostGroupId,
    int Priority,
    string ParametersJson,
    IReadOnlyList<string> RequiredLabels,
    string TriggerKind,
    string IdempotencyKey,
    Guid? AutomationId = null,
    string PlacementMode = "per_task");

/// <summary>
/// Creates the <c>task_runs</c> of a Running run (ORC-03 "Root tasks", design.md "Run start"): tasks
/// without dependencies Queued, all others Waiting; base priority = clamp(run priority + offset, 0, 100);
/// required labels = the automation's ∪ the task's. M4 admission calls <see cref="CreateTasksAsync"/>
/// inside its own transaction. For PinnedRun (ORC-12, M6 design.md decision 11) the run's pin labels are every
/// label any of its tasks requires, so the first host to take a task can run them all, and each task is marked
/// <c>pinned_run</c> so the claim checks the pin only for those.
/// </summary>
public sealed class RunMaterializer(NpgsqlDataSource dataSource, DefinitionGraphCache graphs)
{
    private const string DefinitionSql = "SELECT graph_hash FROM workflow_definitions WHERE id = @id";

    private const string InsertRunSql = """
        INSERT INTO workflow_runs (id, automation_id, definition_id, status, priority, host_group_id, parameters,
                                   trigger_kind, idempotency_key, started_at, placement_mode)
        VALUES (@id, @automationId, @definitionId, 'running', @priority, @groupId, @parameters, @triggerKind,
                @idempotencyKey, now(), @placement)
        """;

    private const string InsertTaskSql = """
        INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, max_attempts,
                               timeout, priority, effective_priority, host_group_id, required_labels, definition_hash,
                               queued_at, pinned_run)
        VALUES (@id, @runId, @key, @status, @rule, @upstream, @maxAttempts, @timeout, @priority, @priority, @groupId,
                @labels, @hash, CASE WHEN @status = 'queued' THEN now() END,
                (SELECT placement_mode = 'pinned_run' FROM workflow_runs WHERE id = @runId))
        """;

    private const string PinLabelsSql = "UPDATE workflow_runs SET pin_labels = @labels WHERE id = @runId";

    public async Task<Guid> StartAsync(NewRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        string hash;
        await using (var definition = new NpgsqlCommand(DefinitionSql, connection, transaction))
        {
            definition.Parameters.AddWithValue("id", run.DefinitionId);
            hash = await definition.ExecuteScalarAsync(ct) as string
                ?? throw new InvalidOperationException($"Workflow definition {run.DefinitionId} does not exist.");
        }

        var runId = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand(InsertRunSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", runId);
            insert.Parameters.AddWithValue("automationId", (object?)run.AutomationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("definitionId", run.DefinitionId);
            insert.Parameters.AddWithValue("priority", (short)run.Priority);
            insert.Parameters.AddWithValue("groupId", run.HostGroupId);
            insert.Parameters.AddWithValue("parameters", NpgsqlDbType.Jsonb, run.ParametersJson);
            insert.Parameters.AddWithValue("triggerKind", run.TriggerKind);
            insert.Parameters.AddWithValue("idempotencyKey", run.IdempotencyKey);
            insert.Parameters.AddWithValue("placement", run.PlacementMode);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await RunEvents.AddAsync(connection, transaction, runId, null, RunEventTypes.RunStarted, null, ct);
        await CreateTasksAsync(connection, transaction, runId, hash, run.HostGroupId, run.Priority, run.RequiredLabels, ct);
        await transaction.CommitAsync(ct);
        return runId;
    }

    /// <summary>Inserts every task of the run, records the queued roots and wakes the group's claimers on commit.</summary>
    public async Task CreateTasksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, string definitionHash, Guid hostGroupId,
        int runPriority, IReadOnlyList<string> automationLabels, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(automationLabels);

        var graph = await graphs.GetAsync(definitionHash, ct);
        await using var batch = new NpgsqlBatch(connection, transaction);
        var pinLabels = new SortedSet<string>(automationLabels, StringComparer.Ordinal);
        foreach (var task in graph.Tasks)
        {
            pinLabels.UnionWith(task.RequiredLabels);
            var status = task.DependsOn.Count == 0 ? TaskRunStatus.Queued : TaskRunStatus.Waiting;
            var command = new NpgsqlBatchCommand(InsertTaskSql);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("runId", runId);
            command.Parameters.AddWithValue("key", task.Key);
            command.Parameters.AddWithValue("status", DbNames.Status(status));
            command.Parameters.AddWithValue("rule", DbNames.Rule(task.TriggerRule));
            command.Parameters.AddWithValue("upstream", task.DependsOn.Count);
            command.Parameters.AddWithValue("maxAttempts", task.Retry.MaxAttempts);
            command.Parameters.AddWithValue("timeout", task.Timeout);
            command.Parameters.AddWithValue("priority", (short)TaskPriority.Base(runPriority, task.PriorityOffset));
            command.Parameters.AddWithValue("groupId", hostGroupId);
            command.Parameters.AddWithValue("labels", automationLabels.Union(task.RequiredLabels, StringComparer.Ordinal).ToArray());
            command.Parameters.AddWithValue("hash", definitionHash);
            batch.BatchCommands.Add(command);

            if (status == TaskRunStatus.Queued)
                batch.BatchCommands.Add(RunEvents.Command(runId, task.Key, RunEventTypes.TaskQueued, new { attempt = 1 }));
        }

        var pin = new NpgsqlBatchCommand(PinLabelsSql);
        pin.Parameters.AddWithValue("runId", runId);
        pin.Parameters.AddWithValue("labels", pinLabels.ToArray());
        batch.BatchCommands.Insert(0, pin);

        batch.BatchCommands.Add(RunEvents.NotifyTasksReady(hostGroupId));
        await batch.ExecuteNonQueryAsync(ct);
    }

    // Task rows after the run row, in key order: the finisher's run -> task order (M7 design.md decision 10).
    private const string LockTasksSql = """
        SELECT id, task_key, status, attempt FROM task_runs
        WHERE workflow_run_id = @runId
        ORDER BY task_key
        FOR UPDATE
        """;

    private const string RearmTaskSql = """
        UPDATE task_runs SET
          status = @status, upstream_remaining = @remaining, upstream_failed = @failed, upstream_unsuccessful = @unsuccessful,
          max_attempts = attempt + @maxAttempts, effective_priority = priority,
          queued_at = CASE WHEN @status = 'queued' THEN now() END,
          finished_at = CASE WHEN @status = 'skipped' THEN now() END,
          started_at = NULL, not_before = NULL, output = NULL, error = NULL,
          lease_host_id = NULL, lease_token = NULL, lease_expires_at = NULL, leased_at = NULL,
          cancel_requested = false, cancel_requested_at = NULL
        WHERE id = @id
        """;

    /// <summary>
    /// Re-arms the tasks of a re-run run that admission just started (WF-10, design.md decision 10), in admission's
    /// transaction: <see cref="RerunPlan"/> picks the tasks that run again and their initial state; each gets its
    /// definition's full retry budget on top of the attempts it already made, its base priority and cleared
    /// output, error and lease. Succeeded and kept Skipped tasks are not written, so their outputs feed the
    /// re-armed tasks through the usual lease query. Wakes the group's claimers on commit.
    /// </summary>
    /// <returns>Whether any task is left to run; when not, the caller finalizes the run.</returns>
    public async Task<bool> RearmTasksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runId, string definitionHash, Guid hostGroupId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var rows = new Dictionary<string, (Guid Id, int Attempt)>(StringComparer.Ordinal);
        var statuses = new Dictionary<string, TaskRunStatus>(StringComparer.Ordinal);
        await using (var lockTasks = new NpgsqlCommand(LockTasksSql, connection, transaction))
        {
            lockTasks.Parameters.AddWithValue("runId", runId);
            await using var reader = await lockTasks.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(1);
                rows[key] = (reader.GetGuid(0), reader.GetInt32(3));
                statuses[key] = DbNames.TaskStatus(reader.GetString(2));
            }
        }

        var graph = await graphs.GetAsync(definitionHash, ct);
        var plan = RerunPlan.Plan(graph, statuses);
        await using var batch = new NpgsqlBatch(connection, transaction);
        var anyLeft = false;
        foreach (var task in plan)
        {
            var (id, attempt) = rows[task.Key];
            var command = new NpgsqlBatchCommand(RearmTaskSql);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("status", DbNames.Status(task.Status));
            command.Parameters.AddWithValue("remaining", task.UpstreamRemaining);
            command.Parameters.AddWithValue("failed", task.UpstreamFailed);
            command.Parameters.AddWithValue("unsuccessful", task.UpstreamUnsuccessful);
            command.Parameters.AddWithValue("maxAttempts", graph[task.Key].Retry.MaxAttempts);
            batch.BatchCommands.Add(command);

            if (task.Status == TaskRunStatus.Queued)
                batch.BatchCommands.Add(RunEvents.Command(runId, task.Key, RunEventTypes.TaskQueued, new { attempt = attempt + 1, rerun = true }));
            else if (task.Status == TaskRunStatus.Skipped)
                batch.BatchCommands.Add(RunEvents.Command(runId, task.Key, RunEventTypes.TaskSkipped, new { rerun = true }));
            anyLeft |= task.Status is TaskRunStatus.Queued or TaskRunStatus.Waiting;
        }

        batch.BatchCommands.Add(RunEvents.NotifyTasksReady(hostGroupId));
        await batch.ExecuteNonQueryAsync(ct);
        return anyLeft;
    }
}
