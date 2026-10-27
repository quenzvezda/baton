using Baton.Domain.Runs;
using Baton.Persistence.Definitions;
using Npgsql;
using NpgsqlTypes;

namespace Baton.Persistence.Queue;

/// <summary>A leased task whose attempt is ending, read under lock by Complete or the reaper.</summary>
internal sealed record EndingAttempt(
    Guid TaskRunId,
    Guid RunId,
    string TaskKey,
    int Attempt,
    int MaxAttempts,
    string DefinitionHash,
    Guid HostGroupId,
    Guid? HostId,
    DateTime? LeasedAt,
    long? LeaseToken,
    bool CancelRequested)
{
    /// <summary>
    /// Columns in the order <see cref="Read"/> expects, for a query over <c>task_runs</c> aliased <c>t</c>.
    /// </summary>
    public const string Columns = """
        t.id, t.workflow_run_id, t.task_key, t.attempt, t.max_attempts, t.definition_hash, t.host_group_id,
        t.lease_host_id, t.leased_at, t.lease_token, t.cancel_requested
        """;

    public static EndingAttempt Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetString(5),
        reader.GetGuid(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7),
        reader.IsDBNull(8) ? null : reader.GetDateTime(8),
        reader.IsDBNull(9) ? null : reader.GetInt64(9),
        reader.GetBoolean(10));
}

/// <summary>
/// Ends an attempt in the caller's transaction (ORC-03, ORC-09, ORC-10, NFR-Auditability): the task's new
/// state, the attempt row and run events; for a final task the DAG advance with skip propagation and the
/// run's outcome (<see cref="RunFinalizer"/>); then the release of <c>group-tasks</c> and, when the run ended,
/// of the limits it held, all in key order (ADR-0009), and the wake-up. The caller holds the run row and the
/// task row (M3 design.md "Lock order").
/// </summary>
internal sealed class TaskFinisher(DefinitionGraphCache graphs)
{
    private const string UpdateTaskSql = """
        UPDATE task_runs SET
          status           = @status,
          output           = @output,
          error            = @error,
          finished_at      = CASE WHEN @final THEN now() END,
          not_before       = CASE WHEN @status = 'retry_wait' THEN now() + @delay END,
          lease_host_id    = NULL,
          lease_expires_at = NULL
        WHERE id = @id
        """;

    private const string UpdateAttemptSql = """
        UPDATE task_attempts SET finished_at = now(), outcome = @outcome, error = @error
        WHERE task_run_id = @id AND attempt = @attempt AND started_at = @leasedAt AND finished_at IS NULL
        """;

    private const string WaitingSql = """
        SELECT task_key, trigger_rule, upstream_remaining, upstream_failed, upstream_unsuccessful
        FROM task_runs
        WHERE workflow_run_id = @runId AND task_key = ANY (@keys) AND status = 'waiting'
        FOR UPDATE
        """;

    private const string AdvanceSql = """
        UPDATE task_runs t SET
          upstream_remaining    = u.remaining,
          upstream_failed       = u.failed,
          upstream_unsuccessful = u.unsuccessful,
          status                = COALESCE(u.status, t.status),
          queued_at             = CASE WHEN u.status = 'queued' THEN now() ELSE t.queued_at END,
          finished_at           = CASE WHEN u.status = 'skipped' THEN now() ELSE t.finished_at END
        FROM unnest(@keys::text[], @remaining::int[], @failed::int[], @unsuccessful::int[], @statuses::text[])
             AS u(key, remaining, failed, unsuccessful, status)
        WHERE t.workflow_run_id = @runId AND t.task_key = u.key
        """;

    /// <param name="outputJson">The handler's output, for a success.</param>
    /// <param name="errorJson">What went wrong, for anything else.</param>
    public async Task FinishAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, EndingAttempt attempt, AttemptResolution resolution,
        string? outputJson, string? errorJson, CancellationToken ct)
    {
        var final = resolution.Status.IsFinal();
        var graph = await graphs.GetAsync(attempt.DefinitionHash, ct);
        var transitions = final
            ? await AdvanceAsync(connection, transaction, graph, attempt, resolution.Status, ct)
            : [];

        await using var batch = new NpgsqlBatch(connection, transaction);

        var task = new NpgsqlBatchCommand(UpdateTaskSql);
        task.Parameters.AddWithValue("id", attempt.TaskRunId);
        task.Parameters.AddWithValue("status", DbNames.Status(resolution.Status));
        task.Parameters.AddWithValue("output", NpgsqlDbType.Jsonb, (object?)outputJson ?? DBNull.Value);
        task.Parameters.AddWithValue("error", NpgsqlDbType.Jsonb, (object?)errorJson ?? DBNull.Value);
        task.Parameters.AddWithValue("final", final);
        task.Parameters.AddWithValue("delay", resolution.RetryDelay);
        batch.BatchCommands.Add(task);

        var row = new NpgsqlBatchCommand(UpdateAttemptSql);
        row.Parameters.AddWithValue("id", attempt.TaskRunId);
        row.Parameters.AddWithValue("attempt", attempt.Attempt);
        row.Parameters.AddWithValue("leasedAt", NpgsqlDbType.TimestampTz, (object?)attempt.LeasedAt ?? DBNull.Value);
        row.Parameters.AddWithValue("outcome", DbNames.Outcome(resolution.Outcome));
        row.Parameters.AddWithValue("error", NpgsqlDbType.Jsonb, (object?)errorJson ?? DBNull.Value);
        batch.BatchCommands.Add(row);

        var details = new { attempt = attempt.Attempt, hostId = attempt.HostId, leaseToken = attempt.LeaseToken };
        batch.BatchCommands.Add(RunEvents.Command(attempt.RunId, attempt.TaskKey, RunEventTypes.Attempt(resolution.Outcome), details));
        var statusEvent = StatusEvent(resolution);
        if (statusEvent is not null)
        {
            batch.BatchCommands.Add(RunEvents.Command(attempt.RunId, attempt.TaskKey, statusEvent,
                new { attempt = attempt.Attempt, retryInSeconds = resolution.RetryDelay.TotalSeconds }));
        }

        if (transitions.Count > 0)
        {
            batch.BatchCommands.Add(AdvanceCommand(attempt.RunId, transitions));
            foreach (var transition in transitions.Where(t => t.NewStatus is not null))
            {
                var type = transition.NewStatus == TaskRunStatus.Queued ? RunEventTypes.TaskQueued : RunEventTypes.TaskSkipped;
                batch.BatchCommands.Add(RunEvents.Command(attempt.RunId, transition.Key, type, new { attempt = 1 }));
            }
        }

        batch.BatchCommands.Add(RunEvents.NotifyTasksReady(attempt.HostGroupId));
        await batch.ExecuteNonQueryAsync(ct);

        var heldByRun = final ? await RunFinalizer.TryFinalizeAsync(connection, transaction, attempt.RunId, ct) : [];
        await LimitRows.ReleaseAsync(connection, transaction, [DbNames.GroupTasksLimit(attempt.HostGroupId), .. heldByRun], ct);
    }

    /// <summary>A second event when the task's new state is not what the attempt's outcome already says.</summary>
    private static string? StatusEvent(AttemptResolution resolution) => resolution.Status switch
    {
        TaskRunStatus.RetryWait => RunEventTypes.TaskRetryScheduled,
        TaskRunStatus.Failed when resolution.Outcome != AttemptOutcome.Failed => RunEventTypes.Attempt(AttemptOutcome.Failed),
        TaskRunStatus.Cancelled when resolution.Outcome != AttemptOutcome.Cancelled => RunEventTypes.Attempt(AttemptOutcome.Cancelled),
        _ => null,
    };

    private static async Task<IReadOnlyList<TaskTransition>> AdvanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DispatchGraph graph, EndingAttempt attempt,
        TaskRunStatus outcome, CancellationToken ct)
    {
        var descendants = Descendants(graph, attempt.TaskKey);
        if (descendants.Count == 0)
            return [];

        var waiting = new Dictionary<string, WaitingTask>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(WaitingSql, connection, transaction))
        {
            cmd.Parameters.AddWithValue("runId", attempt.RunId);
            cmd.Parameters.AddWithValue("keys", descendants.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                waiting[key] = new WaitingTask(key, DbNames.Rule(reader.GetString(1)), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
            }
        }

        return DagAdvance.Advance(graph, waiting, attempt.TaskKey, outcome);
    }

    private static HashSet<string> Descendants(DispatchGraph graph, string key)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(graph.Downstream(key));
        while (pending.TryPop(out var next))
        {
            if (found.Add(next))
            {
                foreach (var child in graph.Downstream(next))
                    pending.Push(child);
            }
        }

        return found;
    }

    private static NpgsqlBatchCommand AdvanceCommand(Guid runId, IReadOnlyList<TaskTransition> transitions)
    {
        var command = new NpgsqlBatchCommand(AdvanceSql);
        command.Parameters.AddWithValue("runId", runId);
        command.Parameters.AddWithValue("keys", transitions.Select(t => t.Key).ToArray());
        command.Parameters.AddWithValue("remaining", transitions.Select(t => t.UpstreamRemaining).ToArray());
        command.Parameters.AddWithValue("failed", transitions.Select(t => t.UpstreamFailed).ToArray());
        command.Parameters.AddWithValue("unsuccessful", transitions.Select(t => t.UpstreamUnsuccessful).ToArray());
        command.Parameters.AddWithValue("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text,
            transitions.Select(t => t.NewStatus is { } s ? DbNames.Status(s) : null).ToArray());
        return command;
    }
}
