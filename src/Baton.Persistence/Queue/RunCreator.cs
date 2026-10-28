using Baton.Domain.Automations;
using Npgsql;
using NpgsqlTypes;

namespace Baton.Persistence.Queue;

/// <summary>One trigger firing of an automation.</summary>
/// <param name="TriggerId">The cron or webhook trigger; <c>null</c> for Run now.</param>
/// <param name="TriggerKind"><c>cron</c>, <c>webhook</c> or <c>manual</c>.</param>
/// <param name="IdempotencyKey">Identifies the firing (ORC-01, design.md "Idempotency key", decisions 3 and 10).</param>
/// <param name="FireTime">The scheduled time of a cron firing.</param>
public sealed record FireRequest(Guid AutomationId, Guid? TriggerId, string TriggerKind, string IdempotencyKey, DateTimeOffset? FireTime = null);

public enum FireOutcome
{
    /// <summary>A Pending run was created.</summary>
    Created,

    /// <summary>The firing was seen before; <see cref="FireResult.RunId"/> is the run it created.</summary>
    Duplicate,

    /// <summary>Skip policy at the limit: no run, a <c>run.skipped</c> event.</summary>
    Skipped,

    AutomationNotFound,
    AutomationDisabled,

    /// <summary>The selected workflow version is not registered.</summary>
    WorkflowNotFound,
}

/// <param name="ReplacedRunId">The run a Replace firing cancelled.</param>
public readonly record struct FireResult(FireOutcome Outcome, Guid? RunId = null, Guid? ReplacedRunId = null);

/// <summary>
/// Turns a firing into at most one Pending run (ORC-01, AUT-02, AUT-07). Cron, Run now and webhooks all come
/// here, in a transaction that holds the automation row, so the active-run count the policy sees cannot change
/// underneath it and a repeated firing finds its run by idempotency key. The run records what admission needs
/// later: the limits to reserve, the automation's labels and run timeout, and the exact definition version
/// ("follow latest" resolves now).
/// </summary>
public sealed class RunCreator(NpgsqlDataSource dataSource)
{
    private const string LockAutomationSql = """
        SELECT enabled, workflow_key, workflow_version, parameters::text, host_group_id, required_labels, priority,
               max_concurrent_runs, concurrency_policy, run_timeout
        FROM automations WHERE id = @id AND deleted_at IS NULL
        FOR UPDATE
        """;

    private const string ExistingSql = "SELECT id FROM workflow_runs WHERE idempotency_key = @key";

    private const string DefinitionSql = """
        SELECT id FROM workflow_definitions
        WHERE key = @key AND (version = @version OR (@version::int IS NULL AND NOT is_retired))
        ORDER BY version DESC
        LIMIT 1
        """;

    private const string ActiveSql = """
        SELECT id, status FROM workflow_runs
        WHERE automation_id = @id AND status IN ('pending','running','cancelling')
        ORDER BY created_at, id
        """;

    private const string InsertRunSql = """
        INSERT INTO workflow_runs (id, automation_id, definition_id, status, priority, host_group_id, parameters, trigger_kind,
                                   idempotency_key, limit_keys, required_labels, run_timeout)
        VALUES (@id, @automationId, @definitionId, 'pending', @priority, @groupId, @parameters, @triggerKind,
                @idempotencyKey, @limitKeys, @labels, @timeout)
        """;

    /// <summary>Fires in a transaction of its own (Run now, webhooks).</summary>
    public async Task<FireResult> FireAsync(FireRequest request, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var result = await FireAsync(connection, transaction, request, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Fires in the caller's transaction (the trigger loop, which already holds the trigger row).</summary>
    internal static async Task<FireResult> FireAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, FireRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        Automation? automation;
        await using (var lockAutomation = new NpgsqlCommand(LockAutomationSql, connection, transaction))
        {
            lockAutomation.Parameters.AddWithValue("id", request.AutomationId);
            await using var reader = await lockAutomation.ExecuteReaderAsync(ct);
            automation = await reader.ReadAsync(ct) ? Automation.Read(reader) : null;
        }

        if (automation is null)
            return new(FireOutcome.AutomationNotFound);

        await using (var existing = new NpgsqlCommand(ExistingSql, connection, transaction))
        {
            existing.Parameters.AddWithValue("key", request.IdempotencyKey);
            if (await existing.ExecuteScalarAsync(ct) is Guid runId)
                return new(FireOutcome.Duplicate, runId);
        }

        if (!automation.Enabled)
            return new(FireOutcome.AutomationDisabled);

        long? definitionId;
        await using (var definition = new NpgsqlCommand(DefinitionSql, connection, transaction))
        {
            definition.Parameters.AddWithValue("key", automation.WorkflowKey);
            definition.Parameters.AddWithValue("version", NpgsqlDbType.Integer, (object?)automation.WorkflowVersion ?? DBNull.Value);
            definitionId = await definition.ExecuteScalarAsync(ct) as long?;
        }

        if (definitionId is null)
            return new(FireOutcome.WorkflowNotFound);

        var active = new List<(Guid Id, string Status)>();
        await using (var activeRuns = new NpgsqlCommand(ActiveSql, connection, transaction))
        {
            activeRuns.Parameters.AddWithValue("id", request.AutomationId);
            await using var reader = await activeRuns.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                active.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        var decision = ConcurrencyPolicies.Decide(automation.Policy, automation.MaxConcurrentRuns, active.Count);
        var fired = new
        {
            triggerId = request.TriggerId,
            triggerKind = request.TriggerKind,
            fireTime = request.FireTime,
            policy = ConcurrencyPolicies.ToDb(automation.Policy),
            activeRuns = active.Count,
        };

        if (decision.Action == FireAction.Skip)
        {
            // No run exists to own the event, so it is filed under the automation's id (design.md "Implementation notes").
            await RunEvents.AddAsync(connection, transaction, request.AutomationId, null, RunEventTypes.RunSkipped, fired, ct);
            return new(FireOutcome.Skipped);
        }

        Guid? replaced = null;
        if (decision.Action == FireAction.ReplaceOldest)
        {
            var oldest = active.FirstOrDefault(r => r.Status != "cancelling");
            if (oldest.Id != Guid.Empty
                && await RunCancellation.CancelAsync(connection, transaction, oldest.Id, RunCancelReason.Replaced, ct)
                    is CancelOutcome.Cancelled or CancelOutcome.Cancelling)
            {
                replaced = oldest.Id;
            }
        }

        var limitKeys = new List<string>
        {
            AutomationRules.WorkflowLimitKey(automation.WorkflowKey),
            DbNames.GroupRunsLimit(automation.HostGroupId),
        };
        if (decision.HoldsAutomationLimit)
            limitKeys.Add(AutomationRules.AutomationLimitKey(request.AutomationId));
        limitKeys.Sort(StringComparer.Ordinal);

        var newRunId = Guid.CreateVersion7();
        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            var insert = new NpgsqlBatchCommand(InsertRunSql);
            insert.Parameters.AddWithValue("id", newRunId);
            insert.Parameters.AddWithValue("automationId", request.AutomationId);
            insert.Parameters.AddWithValue("definitionId", definitionId.Value);
            insert.Parameters.AddWithValue("priority", automation.Priority);
            insert.Parameters.AddWithValue("groupId", automation.HostGroupId);
            insert.Parameters.AddWithValue("parameters", NpgsqlDbType.Jsonb, automation.ParametersJson);
            insert.Parameters.AddWithValue("triggerKind", request.TriggerKind);
            insert.Parameters.AddWithValue("idempotencyKey", request.IdempotencyKey);
            insert.Parameters.AddWithValue("limitKeys", limitKeys.ToArray());
            insert.Parameters.AddWithValue("labels", automation.RequiredLabels);
            insert.Parameters.AddWithValue("timeout", NpgsqlDbType.Interval, (object?)automation.RunTimeout ?? DBNull.Value);
            batch.BatchCommands.Add(insert);
            batch.BatchCommands.Add(RunEvents.Command(newRunId, null, RunEventTypes.RunCreated, new
            {
                fired.triggerId,
                fired.triggerKind,
                fired.fireTime,
                fired.policy,
                fired.activeRuns,
                replacedRunId = replaced,
            }));
            await batch.ExecuteNonQueryAsync(ct);
        }

        return new(FireOutcome.Created, newRunId, replaced);
    }

    private sealed record Automation(
        bool Enabled,
        string WorkflowKey,
        int? WorkflowVersion,
        string ParametersJson,
        Guid HostGroupId,
        string[] RequiredLabels,
        short Priority,
        int MaxConcurrentRuns,
        ConcurrencyPolicy Policy,
        TimeSpan? RunTimeout)
    {
        public static Automation Read(NpgsqlDataReader reader)
        {
            var column = reader.GetString(8);
            if (!ConcurrencyPolicies.TryParse(column, out var policy))
                throw new InvalidOperationException($"Unknown concurrency policy '{column}'.");

            return new(
                reader.GetBoolean(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetString(3),
                reader.GetGuid(4),
                reader.GetFieldValue<string[]>(5),
                reader.GetInt16(6),
                reader.GetInt32(7),
                policy,
                reader.IsDBNull(9) ? null : reader.GetFieldValue<TimeSpan>(9));
        }
    }
}
