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
    Guid? AutomationId = null);

/// <summary>
/// Creates the <c>task_runs</c> of a Running run (ORC-03 "Root tasks", design.md "Run start"): tasks
/// without dependencies Queued, all others Waiting; base priority = clamp(run priority + offset, 0, 100);
/// required labels = the automation's ∪ the task's. M4 admission calls <see cref="CreateTasksAsync"/>
/// inside its own transaction.
/// </summary>
public sealed class RunMaterializer(NpgsqlDataSource dataSource, DefinitionGraphCache graphs)
{
    private const string DefinitionSql = "SELECT graph_hash FROM workflow_definitions WHERE id = @id";

    private const string InsertRunSql = """
        INSERT INTO workflow_runs (id, automation_id, definition_id, status, priority, host_group_id, parameters,
                                   trigger_kind, idempotency_key, started_at)
        VALUES (@id, @automationId, @definitionId, 'running', @priority, @groupId, @parameters, @triggerKind,
                @idempotencyKey, now())
        """;

    private const string InsertTaskSql = """
        INSERT INTO task_runs (id, workflow_run_id, task_key, status, trigger_rule, upstream_remaining, max_attempts,
                               timeout, priority, effective_priority, host_group_id, required_labels, definition_hash,
                               queued_at)
        VALUES (@id, @runId, @key, @status, @rule, @upstream, @maxAttempts, @timeout, @priority, @priority, @groupId,
                @labels, @hash, CASE WHEN @status = 'queued' THEN now() END)
        """;

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
        foreach (var task in graph.Tasks)
        {
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

        batch.BatchCommands.Add(RunEvents.NotifyTasksReady(hostGroupId));
        await batch.ExecuteNonQueryAsync(ct);
    }
}
